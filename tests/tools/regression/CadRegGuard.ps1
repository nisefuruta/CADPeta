# AutoCAD のプロファイル（HKCU のレジストリと .aws）の記録と復元。AutoCAD は起動するだけでプロファイルの値を変え、
# accoreconsole は起動時の作業ディレクトリを LogFilePath に書いて残し、強制終了で FileDialog=0 を残す。
# そこで、試験の前に AutoCAD と DWGCommon のキーの全部の値と .aws を記録し、試験の後に開始時の状態へ戻して一致を確かめる。
# 記録はファイルにも書き、戻して一致したときだけ消す（中断したときは cad_reg_restore.ps1 で戻す）。
# 終了時にほかの AutoCAD が動いていれば、その設定を書き換えないように戻さず、記録を残して失敗にする。
#
# 使い方（ドットソース）:
#   . "$PSScriptRoot\CadRegGuard.ps1"
#   $regGuard = Get-CadRegSnapshot
#   try { ...本体... }
#   finally {
#       $regReport = Restore-CadRegState $regGuard
#       if (-not $regReport.ok) { ...失敗として扱う... }
#   }

. "$PSScriptRoot\..\common\test_helpers.ps1"   # Get-CadPetaInstallRoots、記録の表（Save-RecordBaseline）（インストール先の前後の比較）

$script:CadRegGuardDefaultRoots =@('HKCU:\Software\Autodesk\AutoCAD', 'HKCU:\Software\Autodesk\DWGCommon')
$script:CadRegGuardDefaultRecord = Join-Path $env:LOCALAPPDATA 'CadPeta-e2e\reg-backup\autocad.reg.txt'

# 既定の .aws のフォルダ（AutoCAD の各バージョンの Support\Profiles。AutoIsolate の下は含めない）
function Get-CadRegDefaultAwsRoots {
    @(Get-ChildItem -Path (Join-Path $env:APPDATA 'Autodesk') -Directory -Filter 'AutoCAD 20*' -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Directory -ErrorAction SilentlyContinue } |
        ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Directory -ErrorAction SilentlyContinue } |
        ForEach-Object { Join-Path $_.FullName 'Support\Profiles' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Container })
}

# 終了時に動いている AutoCAD（acad・accoreconsole）。試験の本体の AutoCAD は終了した後に呼ぶ
function Get-CadRegRunningCad { @(Get-Process -Name 'acad', 'accoreconsole' -ErrorAction SilentlyContinue) }

# $ParentId のプロセス（自分の起動したもの。終了済みでよい）が起動した子プロセスの終了を、$TimeoutSeconds まで待つ。
# acad.exe（2024・2026）は終了の直前に accoreconsole を起動し、その子プロセスは acad.exe の 1～3 秒後に終わる。
# 待たずに戻すと、ほかの AutoCAD が動いていると判定して戻さない。戻り値: 上限を過ぎても残っている子プロセス
function Wait-CadRegChildProcesses([int]$ParentId, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$ParentId")
        if ($children.Count -eq 0 -or (Get-Date) -ge $deadline) { return $children }
        Start-Sleep -Milliseconds 200
    }
}

function ConvertTo-CadRegSubKey([string]$root) {
    $p = $root -replace '^HKCU:\\', '' -replace '^HKEY_CURRENT_USER\\', ''
    return $p.TrimEnd('\')
}

function ConvertTo-CadRegData([Microsoft.Win32.RegistryKey]$key, [string]$name) {
    $kind = $key.GetValueKind($name)
    $value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    switch ($kind) {
        'MultiString' { $text = [string]::Join([string][char]0, [string[]]$value); return @("$kind", [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($text))) }
        'Binary' { return @("$kind", [Convert]::ToBase64String([byte[]]$value)) }
        'None' { return @("$kind", [Convert]::ToBase64String([byte[]]$value)) }
        'DWord' { return @("$kind", ([int]$value).ToString([Globalization.CultureInfo]::InvariantCulture)) }
        'QWord' { return @("$kind", ([long]$value).ToString([Globalization.CultureInfo]::InvariantCulture)) }
        default { return @("$kind", [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes([string]$value))) }
    }
}

function ConvertFrom-CadRegData([string]$kind, [string]$data) {
    switch ($kind) {
        'MultiString' {
            $text = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($data))
            if ($text.Length -eq 0) { return , [string[]]@() }
            return , [string[]]($text.Split([char]0))
        }
        'Binary' { return , [byte[]][Convert]::FromBase64String($data) }
        'None' { return , [byte[]][Convert]::FromBase64String($data) }
        'DWord' { return [int]::Parse($data, [Globalization.CultureInfo]::InvariantCulture) }
        'QWord' { return [long]::Parse($data, [Globalization.CultureInfo]::InvariantCulture) }
        default { return [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($data)) }
    }
}

# レジストリを読み、記録を返す（ファイルには書かない）。読めないキーや値があれば例外を投げる
# （一部だけの記録は、戻したつもりで戻っていない状態を作るため）。.aws とインストール先は、test_helpers.ps1 の記録の表（Save-RecordBaseline）で扱う
function Read-CadRegState([string[]]$Roots) {
    $keys = New-Object System.Collections.Generic.List[string]
    $values = @{}
    foreach ($root in $Roots) {
        $sub = ConvertTo-CadRegSubKey $root
        $top = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($sub)
        if ($null -eq $top) { continue }
        $stack = New-Object System.Collections.Generic.Stack[string]
        $top.Dispose()
        $stack.Push($sub)
        while ($stack.Count -gt 0) {
            $path = $stack.Pop()
            $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($path)
            if ($null -eq $key) { throw "レジストリのキーを開けない: HKCU\$path" }
            try {
                $keys.Add($path)
                foreach ($name in $key.GetValueNames()) {
                    $kd = ConvertTo-CadRegData $key $name
                    $values["$path::$name"] = [pscustomobject]@{ Key = $path; Name = $name; Kind = $kd[0]; Data = $kd[1] }
                }
                foreach ($child in $key.GetSubKeyNames()) { $stack.Push("$path\$child") }
            }
            finally { $key.Dispose() }
        }
    }
    [pscustomobject]@{ Roots = $Roots; AwsRoots = @(); Keys = $keys.ToArray(); Values = $values; Baseline = @(); RecordPath = $null; Copies = @(); CreatedDirs = @() }
}

# 記録は一時の名前で書き終えてから正式な名前に変える。途中で失敗したときに END の無い記録が正式な名前で残ると、
# 次の記録は「前回の記録がある」で止まり、戻すだけの処理も読めずに止まる。前の実行の一時のファイルは、環境を変える前に書いたものなので削除する
function Write-CadRegRecord($state, [string]$path) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    $partial = "$path.partial"
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    try { Write-CadRegRecordFile $state $partial }
    catch {
        Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $partial) { throw "記録を書けない: $($_.Exception.Message)（一時のファイルも削除できない: $partial）" }
        throw "記録を書けない: $($_.Exception.Message)"
    }
    [IO.File]::Move($partial, $path)
}

function Write-CadRegRecordFile($state, [string]$path) {
    $b64 = { param($s) [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($s)) }
    $writer = New-Object IO.StreamWriter($path, $false, (New-Object Text.UTF8Encoding $false))
    try {
        foreach ($r in $state.Roots) { $writer.WriteLine("R`t" + (& $b64 $r)) }
        foreach ($r in $state.AwsRoots) { $writer.WriteLine("A`t" + (& $b64 $r)) }
        foreach ($k in $state.Keys) { $writer.WriteLine("K`t" + (& $b64 $k)) }
        foreach ($v in $state.Values.Values) { $writer.WriteLine("V`t" + (& $b64 $v.Key) + "`t" + (& $b64 $v.Name) + "`t" + $v.Kind + "`t" + $v.Data) }
        foreach ($entry in @($state.Baseline | Where-Object { $_.Row.Mode -eq 'Restore' })) {
            foreach ($p in $entry.Backup.Keys) { $writer.WriteLine("F`t" + (& $b64 $p) + "`t" + [Convert]::ToBase64String([byte[]]$entry.Backup[$p])) }
        }
        # 信頼済みのフォルダへのコピー（コピーする前に書く。中断したときに、戻すだけの処理がコピーしたものだけを削除できるように）
        foreach ($c in $state.Copies) { $writer.WriteLine("C`t" + (& $b64 $c)) }
        foreach ($d in $state.CreatedDirs) { $writer.WriteLine("D`t" + (& $b64 $d)) }
        $writer.WriteLine("END")
    }
    finally { $writer.Dispose() }
}

function Read-CadRegRecord([string]$path) {
    $u = { param($s) [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($s)) }
    $roots = New-Object System.Collections.Generic.List[string]
    $awsRoots = New-Object System.Collections.Generic.List[string]
    $keys = New-Object System.Collections.Generic.List[string]
    $values = @{}
    $aws = @{}
    $copies = New-Object System.Collections.Generic.List[string]
    $createdDirs = New-Object System.Collections.Generic.List[string]
    $complete = $false
    foreach ($line in [IO.File]::ReadLines($path)) {
        $f = $line.Split("`t")
        switch ($f[0]) {
            'R' { $roots.Add((& $u $f[1])) }
            'A' { $awsRoots.Add((& $u $f[1])) }
            'K' { $keys.Add((& $u $f[1])) }
            'V' { $k = & $u $f[1]; $n = & $u $f[2]; $values["$k::$n"] = [pscustomobject]@{ Key = $k; Name = $n; Kind = $f[3]; Data = $f[4] } }
            'F' { $aws[(& $u $f[1])] = [Convert]::FromBase64String($f[2]) }
            'C' { $copies.Add((& $u $f[1])) }
            'D' { $createdDirs.Add((& $u $f[1])) }
            'END' { $complete = $true }
        }
    }
    if (-not $complete) { throw "記録のファイルが途中で切れている: $path" }
    # .aws は記録の表の Restore の行に戻す（記録のファイルにはインストール先が無いので、Verify の行は無い）
    $baseline = @($awsRoots | ForEach-Object { New-RecordBaselineFromBackup (New-RecordRow '.aws' $_ 'Restore' '*.aws') $aws })
    [pscustomobject]@{ Roots = $roots.ToArray(); AwsRoots = $awsRoots.ToArray(); Keys = $keys.ToArray(); Values = $values; Baseline = $baseline; RecordPath = $path; Copies = $copies.ToArray(); CreatedDirs = $createdDirs.ToArray() }
}

# $StageToTrusted（試験する DLL と LISP のパス）を信頼済みのフォルダの直下へコピーする計画を返す。DLL はそのフォルダの全ファイル
# （依存の DLL と lang などのサブフォルダ）を $TrustedDotnetFolder の直下へ、.lsp は $TrustedLispFolder の直下へ置く。
# プロファイルの TRUSTEDPATHS（$TrustedPaths）にコピー先のフォルダが無いか、コピー先に同じ名前のファイルがあれば、何も変えずに例外で止める。
function Get-CadRegTrustedCopyPlan([string[]]$StageToTrusted, [string]$TrustedDotnetFolder, [string]$TrustedLispFolder, [string]$TrustedPaths) {
    $copies = New-Object System.Collections.Generic.List[object]
    foreach ($source in $StageToTrusted) {
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "信頼済みのフォルダへコピーするファイルが無い: $source" }
        if ([IO.Path]::GetExtension($source) -ieq '.lsp') {
            $copies.Add([pscustomobject]@{ Source = $source; Target = Join-Path $TrustedLispFolder (Split-Path -Leaf $source); Root = $TrustedLispFolder })
            continue
        }
        $dir = Split-Path -Parent ([IO.Path]::GetFullPath($source))
        foreach ($file in @(Get-ChildItem -LiteralPath $dir -Recurse -File)) {
            $copies.Add([pscustomobject]@{ Source = $file.FullName; Target = Join-Path $TrustedDotnetFolder $file.FullName.Substring($dir.Length).TrimStart('\'); Root = $TrustedDotnetFolder })
        }
    }
    foreach ($root in @($copies | ForEach-Object { $_.Root } | Sort-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "信頼済みのフォルダが無い: $root" }
        if (-not (Test-TrustedPathsContain $TrustedPaths $root)) {
            throw "AutoCAD のプロファイルの TRUSTEDPATHS に $root が無いので、DLL と LISP を読み込めない（TRUSTEDPATHS は変えない）。TRUSTEDPATHS: $TrustedPaths"
        }
    }
    # 同じ名前のファイルの確認と、作る中間のフォルダは、互換 CAD のハーネスと共通（test_helpers.ps1）
    return Get-TrustedFolderCopyPlan $copies.ToArray()
}

# 試験の前に記録する。記録はファイルにも書く。前回の記録が残っていれば、元の値を持つのはその記録だけなので、上書きせずに止める。
# インストール先（$InstallRoots。既定は Get-CadPetaInstallRoots）のファイルの名前と内容も記録する。インストール先は戻さず、
# Restore-CadRegState が比べるだけなので、記録のファイルには書かない。
# $StageToTrusted を渡すと、試験する DLL と LISP を信頼済みのフォルダの直下へコピーする（TRUSTEDPATHS は変えない）。コピーは記録の後に行い、
# コピー先を記録のファイルにも書く（Restore-CadRegState と、中断したときの戻すだけの処理が、コピーしたものだけを削除する）。
# TRUSTEDPATHS は $AcadVersion の版の利用者のプロファイルから読む（$TrustedPaths を渡せば、それを使う。自己試験のため）。
function Get-CadRegSnapshot(
    [string[]]$Roots = $script:CadRegGuardDefaultRoots,
    [string[]]$AwsRoots = $null,
    [string]$RecordPath = $script:CadRegGuardDefaultRecord,
    [string[]]$InstallRoots = $null,
    [string[]]$StageToTrusted = @(),
    [string]$AcadVersion = '',
    [string]$TrustedDotnetFolder = $CadPetaTrustedDotnetFolder,
    [string]$TrustedLispFolder = $CadPetaTrustedLispFolder,
    [string]$TrustedPaths = $null
) {
    if ($null -eq $AwsRoots) { $AwsRoots = Get-CadRegDefaultAwsRoots }
    if ($null -eq $InstallRoots) { $InstallRoots = Get-CadPetaInstallRoots }
    if (Test-Path -LiteralPath $RecordPath) {
        throw "前回の試験で戻していない AutoCAD のプロファイルの記録があります。tests\tools\regression\cad_reg_restore.ps1 で戻してから実行してください: $RecordPath"
    }
    $plan = $null
    if (@($StageToTrusted | Where-Object { $_ }).Count) {
        # [string] の引数は $null を空の文字列にするので、渡されたかどうかで判定する
        if (-not $PSBoundParameters.ContainsKey('TrustedPaths')) {
            if (-not $AcadVersion) { throw '信頼済みのフォルダへコピーするには、TRUSTEDPATHS を読む AutoCAD の版（-AcadVersion）が要る' }
            $TrustedPaths = Get-AcadProfileTrustedPaths $AcadVersion
        }
        $plan = Get-CadRegTrustedCopyPlan @($StageToTrusted | Where-Object { $_ }) $TrustedDotnetFolder $TrustedLispFolder $TrustedPaths
        # 信頼済みのフォルダも前後の比較の対象にする（コピーを削除した後に、元のままであることを確かめる）
        $InstallRoots = @($InstallRoots) + @($TrustedDotnetFolder, $TrustedLispFolder) | Sort-Object -Unique
    }
    # 記録する対象の表: .aws は戻して照合し、インストール先と信頼済みのフォルダは戻さずに違えば失敗にする。読めないファイルがあれば、何も変えずに止まる
    $rows = @($AwsRoots | ForEach-Object { New-RecordRow '.aws' $_ 'Restore' '*.aws' }) + @($InstallRoots | ForEach-Object { New-RecordRow 'インストール先' $_ 'Verify' })
    $baseline = @(Save-RecordBaseline $rows)
    $state = Read-CadRegState $Roots
    $state.AwsRoots = @($AwsRoots)
    $state.Baseline = $baseline
    if ($plan) {
        $state.Copies = @($plan.Copies | ForEach-Object { $_.Target })
        $state.CreatedDirs = @($plan.Dirs)
    }
    Write-CadRegRecord $state $RecordPath
    $state.RecordPath = $RecordPath
    # ここから先は環境を変える（コピー、AutoCAD の起動）。戻し終えるまで、実行前の記録は取らせない
    Set-RecordEnvironmentChanged "Get-CadRegSnapshot（記録: $RecordPath）"
    if ($plan) {
        try { Invoke-TrustedFolderCopy $plan }
        catch {
            # Invoke-TrustedFolderCopy はコピーしたものを削除してから止まる。すべて消えていれば、記録も消す（ほかには何も変えていない）
            if (-not @($state.Copies | Where-Object { Test-Path -LiteralPath $_ }).Count) { Remove-Item -LiteralPath $RecordPath -Force; Clear-RecordEnvironmentChanged }
            throw "$($_.Exception.Message)（記録: $RecordPath）"
        }
    }
    $state
}

function Compare-CadRegState($expected, $actual) {
    $diff = New-Object System.Collections.Generic.List[string]
    $expectedKeys = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($k in $expected.Keys) { [void]$expectedKeys.Add($k) }
    foreach ($k in $actual.Keys) { if (-not $expectedKeys.Contains($k)) { $diff.Add("残ったキー: HKCU\$k") } }
    $actualKeys = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($k in $actual.Keys) { [void]$actualKeys.Add($k) }
    foreach ($k in $expected.Keys) { if (-not $actualKeys.Contains($k)) { $diff.Add("無いキー: HKCU\$k") } }
    foreach ($id in $expected.Values.Keys) {
        $e = $expected.Values[$id]
        $a = $actual.Values[$id]
        if ($null -eq $a) { $diff.Add("無い値: HKCU\$id") }
        elseif ($a.Kind -cne $e.Kind -or $a.Data -cne $e.Data) { $diff.Add("違う値: HKCU\$id") }
    }
    foreach ($id in $actual.Values.Keys) { if (-not $expected.Values.ContainsKey($id)) { $diff.Add("残った値: HKCU\$id") } }
    , $diff.ToArray()
}

# 開始時の状態へ戻し、もう一度読んで一致を確かめる。一致したら記録のファイルを消す。
# 戻り値: @{ actions=戻した内容; mismatch=戻らなかった内容・戻さなかった理由; ok=bool }
function Restore-CadRegState($before, [scriptblock]$GetOtherCadProcesses = ${function:Get-CadRegRunningCad}) {
    $actions = New-Object System.Collections.ArrayList
    $mismatch = New-Object System.Collections.ArrayList
    $others = @(& $GetOtherCadProcesses)
    if ($others.Count -gt 0) {
        [void]$mismatch.Add("ほかの AutoCAD が動いているため、その設定を書き換えないように戻さなかった（PID=$(($others | ForEach-Object { $_.Id }) -join ',')）。すべて終了してから cad_reg_restore.ps1 で戻すこと。記録: $($before.RecordPath)")
        return @{ actions = @($actions); mismatch = @($mismatch); ok = $false }
    }
    # 信頼済みのフォルダへのコピーを、記録したものだけ削除する（元からあるファイルには触れない）
    $copies = if ($before.PSObject.Properties['Copies']) { @($before.Copies) } else { @() }
    $createdDirs = if ($before.PSObject.Properties['CreatedDirs']) { @($before.CreatedDirs) } else { @() }
    if ($copies.Count) {
        foreach ($failure in (Remove-TrustedFolderCopies $copies $createdDirs)) { [void]$mismatch.Add($failure) }
        [void]$actions.Add("信頼済みのフォルダへのコピーを削除: $($copies.Count) 件")
    }
    try {
        $current = Read-CadRegState $before.Roots
        $beforeKeys = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($k in $before.Keys) { [void]$beforeKeys.Add($k) }
        # 増えたキー（親が開始時からあるものと、開始時に無かった監視のキーそのものを、子ごと削除する）。
        # 監視のキーが CAD の初回の起動で作られたときは親も開始時に無いので、監視のキーごと消さないと値が残る
        $rootKeys = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($r in $before.Roots) { [void]$rootKeys.Add((ConvertTo-CadRegSubKey $r)) }
        foreach ($k in $current.Keys) {
            if ($beforeKeys.Contains($k)) { continue }
            $parent = $k.Substring(0, $k.LastIndexOf('\'))
            if (-not $beforeKeys.Contains($parent) -and -not $rootKeys.Contains($k)) { continue }
            [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($k, $false)
            [void]$actions.Add("キーを削除: HKCU\$k")
        }
        # 開始時にあったキーが消えていれば、記録から戻す（次の処理で値も戻す）。試験も AutoCAD もキーを消すことは
        # 確かめられていないので、消えた原因を調べられるように失敗にして記録を残す
        foreach ($k in $before.Keys) {
            $existing = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($k)
            if ($null -eq $existing) {
                [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($k).Dispose()
                [void]$actions.Add("キーを作成: HKCU\$k")
                [void]$mismatch.Add("開始時にあったキーが試験中に消えた（記録から戻した）: HKCU\$k")
            }
            else { $existing.Dispose() }
        }
        # 変わった値・消えた値を戻す
        foreach ($id in $before.Values.Keys) {
            $b = $before.Values[$id]
            $c = $current.Values[$id]
            if ($null -ne $c -and $c.Kind -ceq $b.Kind -and $c.Data -ceq $b.Data) { continue }
            $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($b.Key, $true)
            try { $key.SetValue($b.Name, (ConvertFrom-CadRegData $b.Kind $b.Data), [Microsoft.Win32.RegistryValueKind]$b.Kind) }
            finally { $key.Dispose() }
            [void]$actions.Add("値を戻す: HKCU\$id")
        }
        # 増えた値を削除する（キーごと削除したものは除く）
        foreach ($id in $current.Values.Keys) {
            if ($before.Values.ContainsKey($id)) { continue }
            $c = $current.Values[$id]
            if (-not $beforeKeys.Contains($c.Key)) { continue }
            $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($c.Key, $true)
            try { $key.DeleteValue($c.Name, $false) } finally { $key.Dispose() }
            [void]$actions.Add("値を削除: HKCU\$id")
        }
        # .aws（記録の表の Restore の行）を戻し、レジストリと一緒に照合する
        foreach ($a in (Restore-RecordBaseline $before.Baseline)) { [void]$actions.Add($a) }
        $verify = Read-CadRegState $before.Roots
        foreach ($d in (Compare-CadRegState $before $verify)) { [void]$mismatch.Add($d) }
        foreach ($d in (Compare-RecordBaseline $before.Baseline @('Restore'))) { [void]$mismatch.Add($d) }
    }
    catch {
        [void]$mismatch.Add("戻す処理に失敗: $($_.Exception.Message)")
    }
    # 記録はレジストリと .aws を戻すためのものなので、戻せたかどうかだけで消すかを決める
    $restored = ($mismatch.Count -eq 0)
    if ($restored -and $before.RecordPath -and (Test-Path -LiteralPath $before.RecordPath)) { Remove-Item -LiteralPath $before.RecordPath -Force }
    if (-not $restored -and $before.RecordPath) { [void]$mismatch.Add("記録を残した: $($before.RecordPath)") }
    # 戻し終えたら、実行前の記録を取れるようにする（戻せなかったときは記録が残り、印も残す）
    if ($restored) { Clear-RecordEnvironmentChanged }
    # インストール先と信頼済みのフォルダ（記録の表の Verify の行）は戻さない。試験の前と違えば失敗にする（戻すだけの処理の記録には無いので比べない）
    foreach ($d in (Compare-RecordBaseline $before.Baseline @('Verify'))) { [void]$mismatch.Add($d) }
    $ok = ($mismatch.Count -eq 0)
    @{ actions = @($actions); mismatch = @($mismatch); ok = $ok }
}

# 記録のファイルから戻す（中断した試験の後始末。cad_reg_restore.ps1 が呼ぶ）
function Restore-CadRegRecord([string]$RecordPath = $script:CadRegGuardDefaultRecord,
                              [scriptblock]$GetOtherCadProcesses = ${function:Get-CadRegRunningCad}) {
    Restore-CadRegState (Read-CadRegRecord $RecordPath) -GetOtherCadProcesses $GetOtherCadProcesses
}

# レジストリを復元し、復元した内容があれば件数を出力へ書く（出力は呼び出し側の出力へそのまま流れる）。
# 復元後の報告（Restore-CadRegState の戻り値）は参照 $regReport へ入れる。finally ブロックで呼ぶ。
# $failures（ArrayList）を渡すと、復元の検証に失敗したときの文言をそこへ追加する。
# 戻した内容の要約（件数と最初の 5 件）。戻した内容が無ければ何も返さない。
function Get-CadRegRestoreSummary($report) {
    if ($report.actions.Count -eq 0) { return }
    "レジストリ復元: $($report.actions.Count) 件（" + (($report.actions | Select-Object -First 5) -join '; ') + "）"
}

function Restore-CadRegStateAndReport($regGuard, [ref]$regReport, $failures = $null) {
    $regReport.Value = Restore-CadRegState $regGuard
    Get-CadRegRestoreSummary $regReport.Value
    if ($null -ne $failures -and -not $regReport.Value.ok) {
        [void]$failures.Add("レジストリ保全の復元検証に失敗: " + ($regReport.Value.mismatch -join '; '))
    }
}

# CAD の実行の失敗（$runError）とレジストリ復元の失敗（$regReport）を、両方あれば合流させて投げる。
# 実行の失敗だけなら実行の失敗を、復元の失敗だけなら復元の失敗を投げる。どちらも無ければ何もしない。
function Assert-CadRunAndRegRestore($runError, $regReport) {
    if ($runError -and -not $regReport.ok) {
        # 両方の理由を文に入れ、実行の失敗の例外（スタックを含む）は内側の例外に残す。AggregateException は、Windows PowerShell 5.1
        # （.NET Framework）では内側の例外の文を表示せず、PowerShell 7 では文の後ろに付け足すので使わない
        throw [InvalidOperationException]::new(
            "CAD実行失敗に加えてレジストリ復元にも失敗した。実行の失敗: $($runError.Exception.Message)。復元の失敗: $($regReport.mismatch -join '; ')",
            $runError.Exception)
    }
    if ($runError) { throw $runError }
    if (-not $regReport.ok) { throw "レジストリ保全の復元検証に失敗: $($regReport.mismatch -join '; ')" }
}

# accoreconsole を実行し、その前後でレジストリを保全する（強制終了でも永続 sysvar を復元する）。
# 作業ディレクトリを $stdout のフォルダにする（accoreconsole は図面ごとのログを作業ディレクトリへ書く）。
# 標準出力と標準エラーは $stdout へ UTF-8 で書き、終了コードは参照 $exitCode へ入れる。復元に失敗したら例外を投げる。
# $StageToTrusted は、.scr が読み込む DLL と LISP（Get-AccoreNetloadLines・Get-AccoreLoadLispLines に渡したもの）。信頼済みのフォルダへコピーし、
# 終了後に削除する。TRUSTEDPATHS は $acad の版のプロファイルから読む。
function Invoke-AcadConsoleGuarded([string]$acad, [string]$dwg, [string]$scr, [string]$stdout, [ref]$exitCode, [string[]]$StageToTrusted = @()) {
    $regGuard = Get-CadRegSnapshot -StageToTrusted $StageToTrusted -AcadVersion $(if (@($StageToTrusted).Count) { Get-AcadVersionFromPath $acad } else { '' })
    $regReport = $null
    $runError = $null
    Push-Location -LiteralPath (Split-Path -Parent ([IO.Path]::GetFullPath($stdout)))
    try {
        & $acad /i $dwg /s $scr /l ja-JP 2>&1 | Out-File $stdout -Encoding utf8
        $exitCode.Value = $LASTEXITCODE
    }
    catch { $runError = $_ }
    finally {
        Pop-Location
        Restore-CadRegStateAndReport $regGuard ([ref]$regReport)
    }
    Assert-CadRunAndRegRestore $runError $regReport
}
