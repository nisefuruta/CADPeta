# 回帰ハーネスが共有する小さな補助関数。呼び出し側スクリプトがドットソースで読み込む。
# Log は呼び出し側のスクリプト変数 $logPath を参照する。

# チェック結果（name／ok／detail）を一覧へ追加する。
function Add-Check($list, [string]$name, [bool]$ok, [string]$detail = '') {
    [void]$list.Add(@{ name = $name; ok = $ok; detail = $detail })
}

# Windows PowerShell 5.1 で動くとき、このプロセスの PSModulePath から PowerShell 7 のモジュールのフォルダを外す。
# PowerShell ではない親（cmd、Git Bash、dotnet）を経由して pwsh から 5.1 を起動すると、pwsh の PSModulePath がそのまま渡り、
# 5.1 は PowerShell 7 の Microsoft.PowerShell.Utility を読もうとして、スクリプトで書かれたコマンド（Get-FileHash など）が
# 見つからなくなる（2026-10-25 実測。pwsh から直接 5.1 を起動したときは pwsh が外すので起きない）。
# 変えるのはこのプロセスの環境変数だけで、利用者の設定は変えない。
function Repair-WindowsPowerShellModulePath {
    if ($PSVersionTable.PSEdition -ne 'Desktop') { return }
    $pwshModules = @(
        (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'PowerShell'),
        (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'PowerShell')
    )
    $env:PSModulePath = (@($env:PSModulePath -split ';' | Where-Object {
                $entry = $_
                $entry -and -not @($pwshModules | Where-Object { $entry.StartsWith($_ + '\', [StringComparison]::OrdinalIgnoreCase) }).Count
            }) -join ';')
}

# 環境を変える試験のスクリプトの確認（-WhatIf と -Confirm）の関数は dist/script_confirm.ps1 にある（sign_dist.ps1 と共有する）。
# ここには試験の種類ごとの計画だけを置く。
. "$PSScriptRoot\..\..\..\dist\script_confirm.ps1"

# AutoCAD（accoreconsole・acad.exe）を起動する試験の計画。$Output は試験の出力先
function New-AcadE2ePlan([string]$Name, [string]$Output, [string[]]$More = @()) {
    New-ChangePlan 'AutoCAD のプロファイル（レジストリと .aws）' "$Name を実行し、プロファイルを記録して試験の後に戻す" (@(
            'AutoCAD を起動する。AutoCAD は起動するだけでプロファイルの値を変えるので、試験の前に HKCU\Software\Autodesk\AutoCAD と',
            '  HKCU\Software\Autodesk\DWGCommon の全部の値と .aws を記録し（%LOCALAPPDATA%\CadPeta-e2e\reg-backup）、試験の後に戻して一致を確かめる',
            '読み込む DLL・LISP を信頼済みのフォルダ（C:\Tools\ACAD\dotnet・lisp）の直下へコピーし、終了後にコピーしたものだけを削除する（TRUSTEDPATHS と SECURELOAD は変えない）',
            'CADPeta のインストール先は変えず、試験の前後でファイルの名前と内容が同じことを確かめる',
            "出力先: $(if ($Output) { $Output } else { '既定の作業フォルダ' })") + $More)
}

# 互換 CAD（ARES・IJCAD・BricsCAD）を COM で起動する試験の計画
function New-CompatE2ePlan([string]$Name, [string]$Product, [string]$Output, [string[]]$More = @()) {
    New-ChangePlan "$Product の自動読み込みの登録と設定" "$Name を実行し、登録と profile.xml を試験の間だけ外して戻す" (@(
            "$Product を起動する。インストール済みの CADPeta の自動読み込みの登録（レジストリ、ARES 2023 以前は profile.xml）を外し、",
            '  %LOCALAPPDATA%\CadPeta-e2e\autoload-backup に保存して、CAD の終了後に戻す。ARES は全部の版で profile.xml を保存して元のバイト列へ戻す',
            'ARES は試験する DLL を %AppData%\ARES Standard Edition x64 の下の作業用のフォルダへコピーし、終了後に削除する',
            'FILEDIA・OSMODE などは試験の間だけ変え、終了時に戻す（TRUSTEDPATHS と SECURELOAD は変えない）',
            'CADPeta のインストール先は変えず、試験の前後でファイルの名前と内容が同じことを確かめる',
            "出力先: $(if ($Output) { $Output } else { '既定の作業フォルダ' })") + $More)
}

# CADPeta のインストール先。AutoCAD 版の bundle（install.bat が途中で残す .old と .new を含む）と、互換 CAD 版の register.ps1 が
# ARES の信頼するフォルダへ置く配置フォルダ。E2E は試験の前後でこの内容が変わらないことを確かめる（Save-RecordBaseline と Compare-RecordBaseline）。
function Get-CadPetaInstallRoots {
    @(
        (Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\CADPeta.bundle'),
        (Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\CADPeta.bundle.old'),
        (Join-Path $env:APPDATA 'Autodesk\CADPeta.bundle.new'),
        (Join-Path $env:APPDATA 'ARES Standard Edition x64\CADPeta'),
        # 試験が DLL と LISP をコピーする信頼済みのフォルダ（コピーしたものを削除した後に、元のままであることを確かめる）
        $CadPetaTrustedDotnetFolder,
        $CadPetaTrustedLispFolder
    )
}

# インストール先のうち、acad.exe が起動時に自動で読み込む ApplicationPlugins の下にあるもの（あれば試験の DLL を NETLOAD できない）
function Get-InstalledCadPetaBundles([string[]]$InstallRoots = (Get-CadPetaInstallRoots)) {
    @($InstallRoots | Where-Object { $_ -like '*\Autodesk\ApplicationPlugins\*' -and (Test-Path -LiteralPath $_) })
}

# ---------------------------------------------------------------- フォルダの記録と照合(記録するどのフォルダも、この節の 1 系統で扱う)
# ハッシュは Get-FileSha256Shared(と、その元の Get-StreamSha256)だけで求め、一覧は Get-FolderListing、比較は Compare-FolderListing、
# 読めないファイルの扱いは Read-FileHashResult と Assert-FolderListingReadable の 1 か所ずつにする。比べるのはファイルとフォルダの名前と、
# ファイルの内容のハッシュで、更新時刻とサイズは比べない（利用者の AutoCAD やほかのセッションが bundle を移しただけでも、更新時刻は変わる）。
# Windows PowerShell 5.1 でも動くように .NET の関数で書く（互換 CAD の試験が使う）。

# ストリームの SHA256（Get-FileHash と同じ大文字の 16 進）
function Get-StreamSha256([IO.Stream]$Stream) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Stream)) -replace '-', '') }
    finally { $sha.Dispose() }
}

# ファイルの SHA256。ほかのプロセスが書き込みで開いているファイルも読めるように、共有を許して開く
function Get-FileSha256Shared([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try { return Get-StreamSha256 $stream }
    finally { $stream.Dispose() }
}

# ファイルの SHA256 を { Hash; Failure } で返す（失敗は値として返す）。読めなければ Hash は $null、Failure は理由。
# 読めないことを Hash の欄に文字で入れない（前後とも同じ理由で読めないと文字が一致し、「変わっていない」と判定してしまう）
function Read-FileHashResult([string]$Path) {
    try { return @{ Hash = (Get-FileSha256Shared $Path); Failure = $null } }
    catch { return @{ Hash = $null; Failure = $_.Exception.Message } }
}

# フォルダの中の一覧。Items は相対パス → { Kind = File / Dir; Hash; Failure }、Failures は読めなかったファイルの「相対パス: 理由」の並び、
# Exists はフォルダの有無。$Include（ファイル名のワイルドカード。.aws など）を渡すと、その名前のファイルだけを記録し、フォルダは記録しない
function Get-FolderListing([string]$Dir, [string]$Include = '') {
    $items = @{}
    $failures = New-Object System.Collections.Generic.List[string]
    $exists = Test-Path -LiteralPath $Dir -PathType Container
    if ($exists) {
        $full = [IO.Path]::GetFullPath($Dir).TrimEnd('\')
        foreach ($item in @(Get-ChildItem -LiteralPath $full -Recurse -Force -ErrorAction Stop)) {
            $rel = $item.FullName.Substring($full.Length).TrimStart('\')
            if ($item.PSIsContainer) {
                if (-not $Include) { $items[$rel] = @{ Kind = 'Dir'; Hash = $null; Failure = $null } }
                continue
            }
            if ($Include -and $item.Name -notlike $Include) { continue }
            $read = Read-FileHashResult $item.FullName
            $items[$rel] = @{ Kind = 'File'; Hash = $read.Hash; Failure = $read.Failure }
            if ($read.Failure) { $failures.Add("${rel}: $($read.Failure)") }
        }
    }
    return @{ Dir = $Dir; Include = $Include; Exists = $exists; Items = $items; Failures = @($failures) }
}

# 実行前の記録の一覧に読めないファイルがあれば止める（読めないまま記録すると、実行後と比べられない）
function Assert-FolderListingReadable($Listing, [string]$Label) {
    if (@($Listing.Failures).Count) { throw "$Label の実行前の記録で、読めないファイルがあります（環境を変える前に止めます）: $($Listing.Failures -join ' / ')" }
}

# 2 つの一覧（Get-FolderListing の形）の違いを「ラベル 増えた / 消えた / 変わった / 種類が変わった / 読めない: パス」の配列にする（違いが無ければ空）。
# どちらかの側で読めなかったファイルは、必ず違いにする（読めないファイルを一致としない）
function Compare-FolderListing($Before, $After, [string]$Label) {
    $out = New-Object System.Collections.Generic.List[string]
    $name = { param($rel, $item) $path = Join-Path $Before.Dir $rel; if ($item.Kind -eq 'Dir') { "フォルダ $path" } else { $path } }
    foreach ($rel in @($After.Items.Keys | Sort-Object)) {
        $a = $After.Items[$rel]
        if (-not $Before.Items.ContainsKey($rel)) { $out.Add("$Label 増えた: $(& $name $rel $a)"); continue }
        $b = $Before.Items[$rel]
        if ($b.Kind -ne $a.Kind) { $out.Add("$Label 種類が変わった: $(Join-Path $Before.Dir $rel)"); continue }
        if ($b.Kind -ne 'File') { continue }
        if ($b.Failure -or $a.Failure) { $out.Add("$Label 読めない: $(Join-Path $Before.Dir $rel)（$(@($b.Failure, $a.Failure | Where-Object { $_ }) -join ' / ')）"); continue }
        if ($b.Hash -ne $a.Hash) { $out.Add("$Label 変わった: $(Join-Path $Before.Dir $rel)") }
    }
    foreach ($rel in @($Before.Items.Keys | Sort-Object)) {
        if (-not $After.Items.ContainsKey($rel)) { $out.Add("$Label 消えた: $(& $name $rel $Before.Items[$rel])") }
    }
    if ($Before.Exists -and -not $After.Exists -and $Before.Items.Count -eq 0) { $out.Add("$Label 消えた: フォルダ $($Before.Dir)") }
    if (-not $Before.Exists -and $After.Exists -and $After.Items.Count -eq 0) { $out.Add("$Label 増えた: フォルダ $($Before.Dir)") }
    return , $out.ToArray()
}

# 一覧を、相対パスと SHA256 の行（フォルダは末尾に \）を並べた文字列にする（試験の補助で、2 つの時点の一覧を文字列として比べるため）
function Format-FolderListing($Listing) {
    return ((@($Listing.Items.Keys | Sort-Object | ForEach-Object {
                    $item = $Listing.Items[$_]
                    if ($item.Kind -eq 'Dir') { "$_\" } elseif ($item.Failure) { "$_=<読めない: $($item.Failure)>" } else { "$_=$($item.Hash)" }
                })) -join "`n")
}

# ---------------------------------------------------------------- 実行前の記録の表
# 記録する対象を表の行（New-RecordRow）に並べ、行ごとに扱い（Mode）を決める: Restore（内容をバックアップし、戻して照合する。.aws）、
# Verify（戻さず、違えば失敗にする。インストール先、信頼済みのフォルダ）。実行前の記録は Save-RecordBaseline の 1 か所でまとめて取る。
# 環境を変えた後（Set-RecordEnvironmentChanged の後、Clear-RecordEnvironmentChanged の前）に実行前の記録を取ろうとしたら止める。

$script:RecordEnvironmentChanged = $null

function New-RecordRow([string]$Label, [string]$Dir, [ValidateSet('Restore', 'Verify')][string]$Mode, [string]$Include = '') {
    [pscustomobject]@{ Label = $Label; Dir = $Dir; Mode = $Mode; Include = $Include }
}

# 環境を変え始めたことを記録する（$Label は何が変えたか）。戻し終えたら Clear-RecordEnvironmentChanged を呼ぶ
function Set-RecordEnvironmentChanged([string]$Label) { $script:RecordEnvironmentChanged = $Label }
function Clear-RecordEnvironmentChanged { $script:RecordEnvironmentChanged = $null }

function Assert-RecordEnvironmentUnchanged([string]$Label) {
    if ($script:RecordEnvironmentChanged) {
        throw "環境を変えた後（$($script:RecordEnvironmentChanged)。まだ戻していない）に、実行前の記録を取ろうとしました（$Label）。実行前の記録は、環境を変える前の 1 か所でまとめて取ってください。"
    }
}

# 表の行ごとに実行前の一覧を取る。Restore の行は、ファイルの内容（バイト列）もバックアップする（$_.Backup: フルパス → バイト列）。
# 読めないファイルがあれば、何も変えずに止める
function Save-RecordBaseline([object[]]$Rows) {
    Assert-RecordEnvironmentUnchanged (@($Rows | ForEach-Object { $_.Label }) -join '・')
    foreach ($row in @($Rows | Where-Object { $_ -and $_.Dir })) {
        $listing = Get-FolderListing $row.Dir $row.Include
        Assert-FolderListingReadable $listing $row.Label
        $backup = @{}
        if ($row.Mode -eq 'Restore') {
            foreach ($rel in @($listing.Items.Keys)) {
                if ($listing.Items[$rel].Kind -ne 'File') { continue }
                $path = Join-Path $row.Dir $rel
                $bytes = [IO.File]::ReadAllBytes($path)
                # 記録の値は、バックアップしたバイト列のハッシュにそろえる（一覧を取った後に変わっていても、戻す内容と照合の値が食い違わない）
                $listing.Items[$rel].Hash = Get-StreamSha256 (New-Object IO.MemoryStream(, $bytes))
                $backup[$path] = $bytes
            }
        }
        [pscustomobject]@{ Row = $row; Listing = $listing; Backup = $backup }
    }
}

# バックアップのバイト列（フルパス → バイト列）から、Restore の行の実行前の一覧を作る（記録のファイルから戻すときに使う）
function New-RecordBaselineFromBackup($Row, [Collections.IDictionary]$Backup) {
    $full = [IO.Path]::GetFullPath($Row.Dir).TrimEnd('\')
    $items = @{}
    $mine = @{}
    foreach ($path in @($Backup.Keys)) {
        if (-not $path.StartsWith($full + '\', [StringComparison]::OrdinalIgnoreCase)) { continue }
        $items[$path.Substring($full.Length + 1)] = @{ Kind = 'File'; Hash = (Get-StreamSha256 (New-Object IO.MemoryStream(, [byte[]]$Backup[$path]))); Failure = $null }
        $mine[$path] = $Backup[$path]
    }
    $exists = ($items.Count -gt 0) -or (Test-Path -LiteralPath $Row.Dir -PathType Container)
    [pscustomobject]@{ Row = $Row; Listing = @{ Dir = $Row.Dir; Include = $Row.Include; Exists = $exists; Items = $items; Failures = @() }; Backup = $mine }
}

# 記録した一覧と今の一覧の違い（$Modes の行だけ）
function Compare-RecordBaseline([object[]]$Baseline, [string[]]$Modes = @('Restore', 'Verify')) {
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($entry in @($Baseline | Where-Object { $_ -and $Modes -contains $_.Row.Mode })) {
        try { $after = Get-FolderListing $entry.Row.Dir $entry.Row.Include }
        catch { $out.Add("$($entry.Row.Label) を読めない: $($_.Exception.Message)"); continue }
        foreach ($d in (Compare-FolderListing $entry.Listing $after $entry.Row.Label)) { $out.Add($d) }
    }
    return , $out.ToArray()
}

# Restore の行を実行前の内容へ戻す（違うファイルと無くなったファイルをバックアップから書き、増えたファイルを削除する）。戻した内容の一覧を返す。
# 戻した後の照合は、呼び出し側が Compare-RecordBaseline で行う
function Restore-RecordBaseline([object[]]$Baseline) {
    $actions = New-Object System.Collections.Generic.List[string]
    foreach ($entry in @($Baseline | Where-Object { $_ -and $_.Row.Mode -eq 'Restore' })) {
        $after = Get-FolderListing $entry.Row.Dir $entry.Row.Include
        foreach ($path in @($entry.Backup.Keys)) {
            $rel = $path.Substring([IO.Path]::GetFullPath($entry.Row.Dir).TrimEnd('\').Length + 1)
            $now = $after.Items[$rel]
            if ($now -and -not $now.Failure -and $now.Hash -eq $entry.Listing.Items[$rel].Hash) { continue }
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
            [IO.File]::WriteAllBytes($path, [byte[]]$entry.Backup[$path])
            $actions.Add("$($entry.Row.Label) を戻す: $path")
        }
        foreach ($rel in @($after.Items.Keys)) {
            if ($after.Items[$rel].Kind -ne 'File' -or $entry.Listing.Items.ContainsKey($rel)) { continue }
            $path = Join-Path $entry.Row.Dir $rel
            Remove-Item -LiteralPath $path -Force
            $actions.Add("$($entry.Row.Label) を削除: $path")
        }
    }
    return , $actions.ToArray()
}
# 試験の設定（CADPeta の settings.json）を $DataDir に書き、そのフォルダを返す。CAD を起動するときは CADPETA_DATA_DIR にこのフォルダを
# 渡し、利用者の %APPDATA%\CADPeta の設定を読まず、書かない。既定は今までの試験と同じく、図面のフォルダに PDF・SVG・EMF を
# 今までの名前で出す。キーの名前は CadPetaSettingsStore と同じ（単体試験の CadPetaSettingsTests が名前を固定する）
function Write-E2eSettings([string]$DataDir, [string[]]$Formats = @('pdf', 'svg', 'emf'), [string]$OutputFolder = '.',
                           [string]$FileName = '{drawing}_{tab}_{datetime}', [double]$PngDpi = 300) {
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    $json = [ordered]@{ schemaVersion = 1; outputFolder = $OutputFolder; fileName = $FileName; formats = @($Formats); pngDpi = $PngDpi } |
        ConvertTo-Json -Compress
    [IO.File]::WriteAllText((Join-Path $DataDir 'settings.json'), $json, [Text.UTF8Encoding]::new($false))
    $DataDir
}
# 環境変数（名前 → 値）をこのプロセスに設定して $Action を実行し、どの経路でも元の値（無かったものは無い状態）へ戻す。
# CAD を Start-Process で起動する処理を $Action に渡すと、起動した CAD だけが設定した値を引き継ぐ
function Invoke-WithProcessEnvironment([hashtable]$Variables, [scriptblock]$Action) {
    $saved = @{}
    foreach ($name in $Variables.Keys) {
        $saved[$name] = @{ Exists = (Test-Path -LiteralPath "Env:$name"); Value = [Environment]::GetEnvironmentVariable($name, 'Process') }
        [Environment]::SetEnvironmentVariable($name, [string]$Variables[$name], 'Process')
    }
    try { & $Action }
    finally {
        foreach ($name in $saved.Keys) {
            if ($saved[$name].Exists) { [Environment]::SetEnvironmentVariable($name, [string]$saved[$name].Value, 'Process') }
            else { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        }
    }
}
# 引数配列を Start-Process -ArgumentList 用の 1 本の文字列へ連結する（空白と " を含む要素だけ引用する）。
function ConvertTo-NativeArgString([string[]]$ArgArray) {
    (@($ArgArray) | ForEach-Object {
            $s = [string]$_
            if ($s -eq '') { return '""' }
            if ($s -notmatch '[\s"]') { return $s }
            # Windows の引数の規則（CommandLineToArgvW）: " の直前の \ は数を 2 倍にして 1 つ足し、末尾の \ は 2 倍にする
            $sb = New-Object System.Text.StringBuilder '"'
            $backslashes = 0
            foreach ($ch in $s.ToCharArray()) {
                if ($ch -eq '\') { $backslashes++; continue }
                if ($ch -eq '"') { [void]$sb.Append('\', $backslashes * 2 + 1).Append('"') }
                else { [void]$sb.Append('\', $backslashes).Append($ch) }
                $backslashes = 0
            }
            [void]$sb.Append('\', $backslashes * 2).Append('"')
            $sb.ToString()
        }) -join ' '
}

# $logPath へ追記し、同じ行をホストへ表示する。
# Write-Host を使う（Write-Output は関数の戻り値を汚染する）。
function Log($msg) { $msg | Add-Content $logPath; Write-Host $msg }

# JSONL を 1 行ずつ読み、オブジェクトの配列で返す。ファイルが無ければ空配列。
# 不正な行は例外にする。文言の先頭は -InvalidLabel で変えられる（既定は 'trace JSONL が不正'）。
# CAD の中のプラグインが追記している間にも読むので、ほかのプロセスの書き込みを許して開く
# （[IO.File]::ReadAllLines は書き込みを許さずに開き、追記中は「ほかのプロセスが使用中」で失敗した。ARES 2023、2026-10-28）
function Get-JsonLines([string]$Path, [string]$InvalidLabel = 'trace JSONL が不正') {
    $items = New-Object System.Collections.ArrayList
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return @() }
    $stream = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try { $text = (New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)).ReadToEnd() }
    finally { $stream.Dispose() }
    # プラグインは 1 行を改行まで 1 回で追記する（PromptTrace）。改行で終わっていない最後の行は書き込みの途中なので読まない
    $lines = @($text -split "`r?`n")
    if (-not $text.EndsWith("`n")) { $lines = @($lines | Select-Object -SkipLast 1) }
    $lineNo = 0
    foreach ($line in $lines) {
        $lineNo++
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { [void]$items.Add(($line | ConvertFrom-Json -ErrorAction Stop)) }
        catch { throw "${InvalidLabel}（${Path}:$lineNo）: $($_.Exception.Message)" }
    }
    @($items)
}

# AutoCAD の版（2024／2026／2027）に対応するプラグインのターゲットフレームワークを返す。
# 版と TFM の対応は src/Directory.Build.props の割当てに一致させる。
# 未対応の版は「<$UnknownVersionLabel>: <版>」の例外にする。
function Get-CadPluginTargetFramework([string]$Ver, [string]$UnknownVersionLabel = "未知の版") {
    switch ($Ver) {
        "2024" { "net48" }
        "2026" { "net8.0-windows" }
        "2027" { "net10.0-windows" }
        default { throw "${UnknownVersionLabel}: $Ver" }
    }
}

# 出力先（OutRoot）の絶対パスを返す。ドライブ直下は作業フォルダの削除を巻き込むため、例外にする。
function Resolve-OutRoot([string]$OutRoot) {
    $outRootFull = [IO.Path]::GetFullPath($OutRoot).TrimEnd('\')
    $driveRoot = [IO.Path]::GetPathRoot($outRootFull).TrimEnd('\')
    if (-not $outRootFull -or $outRootFull -eq $driveRoot) { throw "OutRoot にドライブ直下は指定できません: $outRootFull" }
    return $outRootFull
}

# CAD を起動する試験が読み込むプラグインの DLL のパスを返す。DLL は tests/tools/build_e2e.ps1 が、配布物と同じ
# ビルド（dist/dist_build.ps1。構成は Release）で作る。$AresYear は ARES の版（2020・2023 は SDK の違う別の出力）。
# 普段のビルドの DLL を試験しないよう、DLL のフォルダの自作の DLL に開発機のパスか失敗の注入が残っていれば、CAD を起動する前に
# 何も変えずに止める（dist/dll_path_check.ps1 の検査。build_dist.ps1 と同じ関数）。
function Get-E2ePluginPath([ValidateSet('Acad', 'Ares', 'Ijcad', 'Bricscad')][string]$Product, [string]$Tfm, [string]$AresYear = '') {
    $path = Get-E2ePluginBuildPath $Product $Tfm $AresYear
    Assert-E2ePluginBuild $path
    return $path
}

# ビルドしたプラグインの DLL の場所（確かめない）。ARES の net48 の 2020・2023 は SDK が年式別なので、出力も年式別（bin\ares<年>）。
# $Config を省くと配布物と同じ構成（dist_build.ps1 の $DistBuildConfig）
function Get-E2ePluginBuildPath([ValidateSet('Acad', 'Ares', 'Ijcad', 'Bricscad')][string]$Product, [string]$Tfm, [string]$AresYear = '', [string]$Config = '') {
    if (-not $Config) {
        . "$PSScriptRoot\..\..\..\dist\dist_build.ps1"
        $Config = $DistBuildConfig
    }
    $repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
    $binBase = if ($AresYear -in @('2020', '2023')) { "bin\ares$AresYear" } else { 'bin' }
    return (Join-Path $repo "src\CadPeta.Plugin.$Product\$binBase\$Config\$Tfm\CadPeta.Plugin.$Product.dll")
}

# 試験の DLL のビルドの入力（リポジトリの根からのパス）。この内容が変わったら、build_e2e.ps1 で DLL を作り直すまで試験しない
$E2eBuildInputs = @('src', 'dist/dist_build.ps1')

# $Repo の作業ツリーにあるビルドの入力の内容から求めた値（git のツリーの ID）。コミットしていない変更と、git に追加していない
# 新しいファイルも含み、git が無視するファイル（bin、obj）は含まない。一時の索引に入れて求めるので、利用者の索引は変えない
function Get-E2eBuildFingerprint([string]$Repo) {
    # Windows PowerShell 5.1 は、Stop のときに外部のプログラムの標準エラーの出力（git の改行の警告など）を例外にする。終了コードで判定する
    $ErrorActionPreference = 'Continue'
    $index = Join-Path ([IO.Path]::GetTempPath()) ('cadpeta-e2e-index-' + [guid]::NewGuid().ToString('N'))
    $previous = $env:GIT_INDEX_FILE
    try {
        $env:GIT_INDEX_FILE = $index
        $output = & git -C $Repo -c core.safecrlf=false add -A -- @($E2eBuildInputs) 2>&1
        if ($LASTEXITCODE -ne 0) { throw "ビルドの入力を読めない（git add の終了コード $LASTEXITCODE）: $($output -join ' ')" }
        $tree = @(& git -C $Repo write-tree 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "ビルドの入力の値を求められない（git write-tree の終了コード $LASTEXITCODE）: $($tree -join ' ')" }
        return ([string]$tree[0]).Trim()
    }
    finally {
        if ($null -eq $previous) { Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue } else { $env:GIT_INDEX_FILE = $previous }
        if (Test-Path -LiteralPath $index) { Remove-Item -LiteralPath $index -Force }
    }
}

function Get-E2eBuildRecordPath([string]$OutRoot) { Join-Path $OutRoot 'e2e-build-source.txt' }

# build_e2e.ps1 が、ビルドの前に求めた $Fingerprint を出力の根に書く（一時の名前で書き終えてから正式の名前に変える）
function Write-E2eBuildRecord([string]$OutRoot, [string]$Fingerprint) {
    $path = Get-E2eBuildRecordPath $OutRoot
    $partial = "$path.partial"
    [IO.File]::WriteAllText($partial, $Fingerprint)
    Move-Item -LiteralPath $partial -Destination $path -Force
}

# $OutRoot の DLL が、今のビルドの入力から作ったものであることを確かめる。記録が無いか、記録の後にビルドの入力が変わっていれば止める
function Assert-E2eBuildRecordCurrent([string]$OutRoot, [string]$Repo) {
    $hint = '先に pwsh -File tests/tools/build_e2e.ps1 を実行して、今のソースから DLL を作る'
    $record = Get-E2eBuildRecordPath $OutRoot
    if (-not (Test-Path -LiteralPath $record)) { throw "試験の DLL をどのソースから作ったかの記録が無い: $record（$hint）" }
    $built = [IO.File]::ReadAllText($record).Trim()
    $current = Get-E2eBuildFingerprint $Repo
    if ($built -ne $current) {
        throw "試験の DLL は今のソースから作ったものではない（DLL を作った後に $($E2eBuildInputs -join ' か ') が変わった。記録 $built、今 $current）: $OutRoot（$hint）"
    }
}

# $Path のプラグインの DLL が、配布物と同じビルド（build_e2e.ps1）で作ったものであることを確かめる。
# 無いか、DLL のフォルダの自作の DLL に開発機のパスか失敗の注入が残っていれば止める
# 検査（dll_path_check.ps1）は PowerShell 7 の System.Reflection.Metadata を使う。互換 CAD の試験は COM のために
# Windows PowerShell 5.1 で動くので、そのときは検査だけを pwsh で実行し、終了コードで合否を受け取る
# DLL が今のソースから作ったものか（Assert-E2eBuildRecordCurrent）も確かめる。
function Assert-E2ePluginBuild([string]$Path) {
    $check = "$PSScriptRoot\..\..\..\dist\dll_path_check.ps1"
    $hint = '先に pwsh -File tests/tools/build_e2e.ps1 を実行して、配布物と同じビルドの DLL を作る'
    if (-not (Test-Path -LiteralPath $Path)) { throw "試験するプラグインの DLL が無い: $Path（$hint）" }
    $dir = Split-Path -Parent $Path
    # DLL は <出力の根>\<TFM>\ にあり、build_e2e.ps1 は記録を出力の根に書く
    Assert-E2eBuildRecordCurrent (Split-Path -Parent $dir) (Resolve-Path "$PSScriptRoot\..\..\..").Path
    if ($PSVersionTable.PSEdition -eq 'Core') {
        . $check
        try { Assert-ReleaseOwnAssemblies $dir "試験の DLL" }
        catch { throw "$($_.Exception.Message)`n普段のビルドの DLL は試験しない（$hint）" }
        return
    }
    $script = "& { . '$($check.Replace("'", "''"))'; Assert-ReleaseOwnAssemblies '$($dir.Replace("'", "''"))' '試験の DLL' }"
    $output = & pwsh -NoProfile -NonInteractive -Command $script 2>&1
    if ($LASTEXITCODE -ne 0) { throw (($output | Out-String).Trim() + "`n普段のビルドの DLL は試験しない（$hint）") }
}

# TRUSTEDPATHS と SECURELOAD は変えない（一時的に変えて戻す形も使わない。プロファイルに保存され、利用者の AutoCAD の安全の設定になるため）。
# 試験は、読み込む DLL のフォルダと LISP を、利用者のプロファイルで信頼済みのフォルダの直下へコピーして読み込み、終了時にコピーしたものだけを
# 削除する（CadRegGuard.ps1 の Get-CadRegSnapshot -StageToTrusted。TRUSTEDPATHS の項目の末尾が \... でなければ下のフォルダは信頼されないので、直下へ置く）。
$CadPetaTrustedDotnetFolder = 'C:\Tools\ACAD\dotnet'
$CadPetaTrustedLispFolder = 'C:\Tools\ACAD\lisp'

# .scr で $Dll を NETLOAD する行。読み込むのは信頼済みのフォルダへコピーした DLL（-StageToTrusted に $Dll を渡す）
function Get-AccoreNetloadLines([string]$Dll) {
    @("(command `"_.NETLOAD`" `"$((Join-Path $CadPetaTrustedDotnetFolder (Split-Path -Leaf $Dll)) -replace '\\', '/')`")")
}

# .scr で $Lisp を読み込む行。読み込むのは信頼済みのフォルダへコピーした LISP（-StageToTrusted に $Lisp を渡す）
function Get-AccoreLoadLispLines([string]$Lisp) {
    @("(load `"$((Join-Path $CadPetaTrustedLispFolder (Split-Path -Leaf $Lisp)) -replace '\\', '/')`")")
}

# 信頼済みのフォルダへコピーしたファイル（$Copies）だけを削除し、コピーで作ったフォルダを（空なら）削除する。消せなかったものを文の配列で返す。
# AutoCAD の終了の直後は DLL が開かれていることがあるので、0.5 秒の間隔で 20 回までやり直す。
function Remove-TrustedFolderCopies([string[]]$Copies, [string[]]$Dirs) {
    $failures = New-Object System.Collections.Generic.List[string]
    foreach ($copy in $Copies) {
        $lastError = $null
        for ($i = 0; $i -lt 20 -and (Test-Path -LiteralPath $copy); $i++) {
            try { Remove-Item -LiteralPath $copy -Force -ErrorAction Stop } catch { $lastError = $_.Exception.Message; Start-Sleep -Milliseconds 500 }
        }
        if (Test-Path -LiteralPath $copy) { $failures.Add("信頼済みのフォルダへコピーしたファイルを削除できない: ${copy}: $lastError") }
    }
    # フォルダの列挙・削除の失敗（アクセス拒否など）も結果に入れ、呼び出し側の後の復元（レジストリなど）を続けられるようにする
    foreach ($dir in @($Dirs | Sort-Object Length -Descending)) {
        try {
            if ((Test-Path -LiteralPath $dir) -and -not @(Get-ChildItem -LiteralPath $dir -Force -ErrorAction Stop).Count) { Remove-Item -LiteralPath $dir -Force -ErrorAction Stop }
        }
        catch { $failures.Add("信頼済みのフォルダへのコピーで作ったフォルダを削除できない: ${dir}: $($_.Exception.Message)") }
    }
    return , $failures.ToArray()
}

# 信頼済みのフォルダへコピーする計画を作る。$Copies は Source（コピー元）、Target（コピー先）、Root（信頼済みのフォルダ）を持つ。
# 同じコピー先が 2 つあれば、内容が同じときだけ 1 つにまとめ、違えば止める。コピー先に同じ名前のファイルがあれば、何もコピーせずに止める
# （自分のコピーだけを削除できるように）。Dirs は、コピーで作るフォルダ（Root より下の、まだ無いすべてのフォルダ。中間のフォルダを含む）。
function Get-TrustedFolderCopyPlan([object[]]$Copies) {
    $unique = New-Object System.Collections.Generic.List[object]
    foreach ($group in @($Copies | Group-Object { $_.Target.ToLowerInvariant() })) {
        $hashes = @($group.Group | ForEach-Object { (Get-FileHash -LiteralPath $_.Source -Algorithm SHA256).Hash } | Sort-Object -Unique)
        if ($hashes.Count -gt 1) { throw "信頼済みのフォルダへ、同じ名前で内容の違うファイルを 2 つコピーしようとした: $($group.Group[0].Target)（$(($group.Group | ForEach-Object { $_.Source }) -join ', ')）" }
        $unique.Add($group.Group[0])
    }
    $existing = @($unique | Where-Object { Test-Path -LiteralPath $_.Target } | ForEach-Object { $_.Target })
    if ($existing.Count) { throw "信頼済みのフォルダに同じ名前のファイルがあるので、何もコピーせずに止める（自分のコピーだけを削除できるように）: $($existing -join ', ')" }
    $dirs = New-Object System.Collections.Generic.List[string]
    foreach ($copy in $unique) {
        for ($d = Split-Path -Parent $copy.Target; $d.Length -gt $copy.Root.TrimEnd('\').Length; $d = Split-Path -Parent $d) {
            if (-not (Test-Path -LiteralPath $d) -and -not $dirs.Contains($d)) { $dirs.Add($d) }
        }
    }
    [pscustomobject]@{ Copies = $unique.ToArray(); Dirs = @($dirs | Sort-Object Length) }
}

# Get-TrustedFolderCopyPlan の計画のとおりにフォルダを作ってコピーする。途中で失敗したら、コピーしたものと作ったフォルダを削除してから例外にする。
function Invoke-TrustedFolderCopy($Plan) {
    try {
        foreach ($dir in $Plan.Dirs) { New-Item -ItemType Directory -Path $dir -WhatIf:$false | Out-Null }
        foreach ($copy in $Plan.Copies) { Copy-Item -LiteralPath $copy.Source -Destination $copy.Target -WhatIf:$false }
    }
    catch {
        $undo = Remove-TrustedFolderCopies @($Plan.Copies | ForEach-Object { $_.Target }) @($Plan.Dirs)
        throw "信頼済みのフォルダへコピーできない: $($_.Exception.Message)$(if ($undo.Count) { "（削除できなかったもの: $($undo -join '; ')）" })"
    }
}

# AutoCAD の COM の ProgID（AutoCAD.Application.25.1 など）から AutoCAD の版を返す。分からなければ例外にする。
function Get-AcadVersionFromProgId([string]$ProgId) {
    $release = if ($ProgId -match '^AutoCAD\.Application\.(\d+\.\d+)$') { $Matches[1] } else { '' }
    switch ($release) { '24.3' { '2024' } '25.0' { '2025' } '25.1' { '2026' } '26.0' { '2027' } default { throw "AutoCAD の版が分からない ProgID: $ProgId" } }
}

# accoreconsole・acad.exe のパス（C:\Program Files\Autodesk\AutoCAD 2026\... など）から AutoCAD の版を返す。分からなければ例外にする。
function Get-AcadVersionFromPath([string]$Path) {
    if ($Path -match 'AutoCAD (\d{4})') { return $Matches[1] }
    throw "AutoCAD の版が分からないパス: $Path"
}

# AutoCAD の版（2024 など）の、利用者の現在のプロファイルの TRUSTEDPATHS を読む（書き換えない）。
# HKCU\Software\Autodesk\AutoCAD\<リリース>\CurVer の製品の、Profiles の既定の値のプロファイルの Variables の値。読めなければ例外にする。
function Get-AcadProfileTrustedPaths([string]$Ver) {
    $release = switch ($Ver) { '2024' { 'R24.3' } '2025' { 'R25.0' } '2026' { 'R25.1' } '2027' { 'R26.0' } default { throw "TRUSTEDPATHS を読む AutoCAD の版が分からない: $Ver" } }
    $base = "HKCU:\Software\Autodesk\AutoCAD\$release"
    $product = (Get-ItemProperty -LiteralPath $base -Name CurVer -ErrorAction Stop).CurVer
    $profiles = Get-Item -LiteralPath "$base\$product\Profiles" -ErrorAction Stop
    $profile = [string]$profiles.GetValue('')
    if (-not $profile) { throw "AutoCAD $Ver の現在のプロファイルが分からない: $($profiles.Name)" }
    $variables = Get-Item -LiteralPath "$base\$product\Profiles\$profile\Variables" -ErrorAction Stop
    return [string]$variables.GetValue('TRUSTEDPATHS', '')
}

# TRUSTEDPATHS（; で区切った一覧）が $Folder を信頼しているか。末尾の \ と大文字・小文字の違いは同じとみなし、
# 末尾が \... の項目はそのフォルダと下のフォルダを信頼する。環境変数は展開して比べる。
function Test-TrustedPathsContain([string]$TrustedPaths, [string]$Folder) {
    $target = [IO.Path]::GetFullPath($Folder).TrimEnd('\')
    foreach ($item in @($TrustedPaths -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
        $expanded = [Environment]::ExpandEnvironmentVariables($item)
        $recursive = $expanded.EndsWith('\...')
        if ($recursive) { $expanded = $expanded.Substring(0, $expanded.Length - 4) }
        $base = [IO.Path]::GetFullPath($expanded).TrimEnd('\')
        if ([string]::Equals($base, $target, [StringComparison]::OrdinalIgnoreCase)) { return $true }
        if ($recursive -and $target.StartsWith($base + '\', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}