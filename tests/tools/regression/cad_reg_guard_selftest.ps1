# CadRegGuard の約束を、AutoCAD の実際のプロファイルに触れず、一時的な HKCU のキーと一時フォルダの .aws で確かめる。
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\common\test_helpers.ps1"   # ConvertTo-NativeArgString（Start-Process の引数を引用符で囲む）
. "$PSScriptRoot\CadRegGuard.ps1"

$testRoot = "HKCU:\Software\CADPeta\Tests\CadRegGuard_$PID"
$general = Join-Path $testRoot 'R26.0\Profiles\<<P>>\General Configuration'
$editor = Join-Path $testRoot 'R26.0\Profiles\<<P>>\Editor Configuration'
$work = Join-Path ([IO.Path]::GetTempPath()) "CadRegGuard_$PID"
$awsRoot = Join-Path $work 'Profiles'
$record = Join-Path $work 'record\autocad.reg.txt'
$checked = New-Object System.Collections.Generic.List[string]
$noOtherCad = { @() }

function New-Fixture {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
    New-Item -Path $general -Force | Out-Null
    New-Item -Path $editor -Force | Out-Null
    New-ItemProperty -Path $general -Name FileDialog -Value 0 -PropertyType DWord | Out-Null
    New-ItemProperty -Path $general -Name OSMODE -Value 4133 -PropertyType DWord | Out-Null
    New-ItemProperty -Path $general -Name TRUSTEDPATHS -Value 'before' -PropertyType String | Out-Null
    New-ItemProperty -Path $editor -Name LogFilePath -Value '%USERPROFILE%\AppData\Local\Autodesk\jpn\' -PropertyType ExpandString | Out-Null
    New-ItemProperty -Path $editor -Name Recent -Value @('a', 'b') -PropertyType MultiString | Out-Null
    New-ItemProperty -Path $editor -Name Blob -Value ([byte[]](1, 2, 3)) -PropertyType Binary | Out-Null
    New-ItemProperty -Path $editor -Name Negative -Value -2 -PropertyType DWord | Out-Null
    New-ItemProperty -Path $editor -Name NegativeQ -Value ([long]::MinValue) -PropertyType QWord | Out-Null
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
    New-Item -ItemType Directory -Force (Join-Path $awsRoot 'P') | Out-Null
    [IO.File]::WriteAllText((Join-Path $awsRoot 'FixedProfile.aws'), 'fixed-before')
    [IO.File]::WriteAllText((Join-Path $awsRoot 'P\Profile.aws'), 'profile-before')
}

function New-TestSnapshot { Get-CadRegSnapshot -Roots @($testRoot) -AwsRoots @($awsRoot) -RecordPath $record }

function Assert-Original([string]$label) {
    $g = Get-Item -LiteralPath $general
    $e = Get-Item -LiteralPath $editor
    $checks = @(
        @($g.GetValue('FileDialog'), 0, $g.GetValueKind('FileDialog'), 'DWord'),
        @($g.GetValue('OSMODE'), 4133, $g.GetValueKind('OSMODE'), 'DWord'),
        @($g.GetValue('TRUSTEDPATHS'), 'before', $g.GetValueKind('TRUSTEDPATHS'), 'String'),
        @($e.GetValue('LogFilePath', $null, 'DoNotExpandEnvironmentNames'), '%USERPROFILE%\AppData\Local\Autodesk\jpn\', $e.GetValueKind('LogFilePath'), 'ExpandString'),
        @(($e.GetValue('Recent') -join '|'), 'a|b', $e.GetValueKind('Recent'), 'MultiString'),
        @(($e.GetValue('Blob') -join ','), '1,2,3', $e.GetValueKind('Blob'), 'Binary'),
        @($e.GetValue('Negative'), -2, $e.GetValueKind('Negative'), 'DWord'),
        @($e.GetValue('NegativeQ'), [long]::MinValue, $e.GetValueKind('NegativeQ'), 'QWord'))
    foreach ($c in $checks) {
        if ("$($c[0])" -cne "$($c[1])") { throw "${label}: 値が戻っていない: 期待「$($c[1])」実「$($c[0])」" }
        if ("$($c[2])" -cne $c[3]) { throw "${label}: 型が戻っていない: 期待 $($c[3]) 実 $($c[2])" }
    }
    foreach ($n in 'SECURELOAD') { if ($null -ne $g.GetValue($n)) { throw "${label}: 開始時に無かった $n が残った" } }
    if (Test-Path -LiteralPath (Join-Path $testRoot 'R26.0\Profiles\<<P>>\Added')) { throw "${label}: 開始時に無かったキーが残った" }
    if ([IO.File]::ReadAllText((Join-Path $awsRoot 'FixedProfile.aws')) -cne 'fixed-before') { throw "${label}: .aws が戻っていない" }
    if (Test-Path -LiteralPath (Join-Path $awsRoot 'P\New.aws')) { throw "${label}: 開始時に無かった .aws が残った" }
}

function Set-Pollution {
    Set-ItemProperty -Path $general -Name FileDialog -Value 1
    Set-ItemProperty -Path $general -Name OSMODE -Value 0
    Remove-ItemProperty -Path $general -Name TRUSTEDPATHS
    New-ItemProperty -Path $general -Name SECURELOAD -Value 0 -PropertyType DWord | Out-Null
    Set-ItemProperty -Path $editor -Name LogFilePath -Value 'C:\work\scratchpad\' -Type ExpandString
    Set-ItemProperty -Path $editor -Name Recent -Value @('A', 'b') -Type MultiString
    Set-ItemProperty -Path $editor -Name Blob -Value ([byte[]](9)) -Type Binary
    Set-ItemProperty -Path $editor -Name Negative -Value 5 -Type DWord
    Set-ItemProperty -Path $editor -Name NegativeQ -Value 5 -Type QWord
    New-Item -Path (Join-Path $testRoot 'R26.0\Profiles\<<P>>\Added\Child') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $awsRoot 'FixedProfile.aws'), 'fixed-after')
    [IO.File]::WriteAllText((Join-Path $awsRoot 'P\New.aws'), 'new')
}

try {
    # 1〜6, 9: 変更・削除・追加の値、LogFilePath、追加のキー、型、大文字と小文字だけの違い、FileDialog=0、.aws を開始時の状態へ戻す
    New-Fixture
    $before = New-TestSnapshot
    Set-Pollution
    $report = Restore-CadRegState $before -GetOtherCadProcesses $noOtherCad
    if (-not $report.ok) { throw "復元が失敗を返した: $($report.mismatch -join '; ')" }
    Assert-Original '通常の復元'
    if (Test-Path -LiteralPath $record) { throw '戻して一致したのに記録が残った' }
    $checked.Add('値の変更・削除・追加、別のキーの LogFilePath、追加のキー、型（DWord と QWord の負の値・ExpandString を展開せず・MultiString・Binary）、大文字と小文字だけの違い、FileDialog=0、.aws の変更と追加を、開始時の状態へ戻し、記録を消す')

    # 7: 記録が残っている間は次の記録を始めず、戻すだけの処理で戻す
    New-Fixture
    $before = New-TestSnapshot
    Set-Pollution
    $threw = $false
    try { [void](New-TestSnapshot) } catch { $threw = $true }
    if (-not $threw) { throw '前回の記録が残っているのに次の記録を始めた' }
    $restoreOnly = Restore-CadRegRecord $record -GetOtherCadProcesses $noOtherCad
    if (-not $restoreOnly.ok) { throw "戻すだけの処理が失敗した: $($restoreOnly.mismatch -join '; ')" }
    Assert-Original '戻すだけの処理'
    if (Test-Path -LiteralPath $record) { throw '戻すだけの処理のあとも記録が残った' }
    $checked.Add('記録をファイルに残し、残っている間は次の記録を始めず、戻すだけの処理で戻して記録を消す')

    # 記録のファイルを書く途中で失敗したとき: END の無い記録を正式な名前で残さない（残ると、次の記録は「前回の記録がある」で止まり、
    # 戻すだけの処理は「途中で切れている」で止まって、手で消すまで試験を実行できない）。R・K・V の行を書いた後の F の行で、
    # バイト列に変換できないバックアップの値で失敗させる
    New-Fixture
    $broken = Read-CadRegState @($testRoot)
    $broken.Baseline = @([pscustomobject]@{ Row = (New-RecordRow '.aws' $awsRoot 'Restore'); Backup = @{ (Join-Path $awsRoot 'FixedProfile.aws') = 'not-bytes' } })
    $threw = $false
    try { Write-CadRegRecord $broken $record } catch { $threw = $true }
    if (-not $threw) { throw '記録を書く途中の失敗が例外にならなかった' }
    if (Test-Path -LiteralPath $record) { throw '記録を書く途中で失敗したのに、正式な名前の記録が残った' }
    $after = New-TestSnapshot
    $report = Restore-CadRegState $after -GetOtherCadProcesses $noOtherCad
    if (-not $report.ok) { throw "記録を書く途中で失敗した後の記録と復元が失敗した: $($report.mismatch -join '; ')" }
    $checked.Add('記録のファイルを書く途中で失敗したら、正式な名前の記録を残さず、次の記録を始められる')

    # 8: ほかの AutoCAD が動いていれば、戻さずに記録を残して失敗にする
    New-Fixture
    $before = New-TestSnapshot
    Set-Pollution
    $blocked = Restore-CadRegState $before -GetOtherCadProcesses { @([pscustomobject]@{ Id = 4242; ProcessName = 'acad' }) }
    if ($blocked.ok) { throw 'ほかの AutoCAD が動いているのに成功を返した' }
    if ((Get-Item -LiteralPath $general).GetValue('OSMODE') -ne 0) { throw 'ほかの AutoCAD が動いているのに戻した' }
    if (-not (Test-Path -LiteralPath $record)) { throw 'ほかの AutoCAD が動いているのに記録を消した' }
    $later = Restore-CadRegRecord $record -GetOtherCadProcesses $noOtherCad
    if (-not $later.ok) { throw "あとで戻す処理が失敗した: $($later.mismatch -join '; ')" }
    Assert-Original 'ほかの AutoCAD の終了後'
    $checked.Add('ほかの AutoCAD が動いていれば戻さずに記録を残して失敗にし、あとで戻すだけの処理で戻せる')

    # 開始時にあったキーが試験中に消えたとき: 記録から戻すが、消えた原因が分からないので失敗にして記録を残す。
    # 戻すだけの処理をもう一度実行すれば、一致して記録を消す
    New-Fixture
    $before = New-TestSnapshot
    Remove-Item -LiteralPath $editor -Recurse -Force
    Set-ItemProperty -Path $general -Name OSMODE -Value 0
    $vanished = Restore-CadRegState $before -GetOtherCadProcesses $noOtherCad
    Assert-Original '消えたキー'
    if ($vanished.ok) { throw '開始時にあったキーが消えたのに成功を返した' }
    if (-not @($vanished.mismatch | Where-Object { $_ -like '*消えた*Editor Configuration*' }).Count) { throw "消えたキーを失敗の一覧に入れなかった: $($vanished.mismatch -join '; ')" }
    if (-not (Test-Path -LiteralPath $record)) { throw '開始時にあったキーが消えたのに記録を消した' }
    $vanishedLater = Restore-CadRegRecord $record -GetOtherCadProcesses $noOtherCad
    if (-not $vanishedLater.ok) { throw "消えたキーを戻した後の戻すだけの処理が失敗した: $($vanishedLater.mismatch -join '; ')" }
    if (Test-Path -LiteralPath $record) { throw '消えたキーを戻した後の戻すだけの処理で記録を消さなかった' }
    $checked.Add('開始時にあったキーが試験中に消えたときは、記録から戻したうえで失敗にして記録を残し、戻すだけの処理をもう一度実行すれば記録を消す')

    # CAD の実行と復元の両方が失敗したとき: 投げる例外の文に、両方の理由が 1 回ずつ入る。互換 CAD の COM の試験が使う
    # Windows PowerShell 5.1 と、この自己試験の PowerShell 7 の両方で確かめる（AggregateException は 5.1 では内側の文を表示せず、7 では付け足す）
    $assertScript = Join-Path $work 'assert_both.ps1'
    [IO.File]::WriteAllText($assertScript, @"
`$ErrorActionPreference = 'Stop'
. '$PSScriptRoot\CadRegGuard.ps1'
Repair-WindowsPowerShellModulePath
try { throw 'body-fail' } catch { `$e = `$_ }
try { Assert-CadRunAndRegRestore `$e @{ ok = `$false; mismatch = @('reg-fail') } } catch { 'MSG=' + `$_.Exception.Message }
"@, [Text.UTF8Encoding]::new($true))
    foreach ($shell in @((Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'), (Get-Process -Id $PID).Path)) {
        $assertOut = & $shell -NoProfile -ExecutionPolicy Bypass -File $assertScript
        $assertMsg = @($assertOut | Where-Object { "$_" -like 'MSG=*' }) -join ''
        foreach ($reason in 'body-fail', 'reg-fail') {
            if (([regex]::Matches($assertMsg, $reason)).Count -ne 1) { throw "実行と復元の両方の失敗の理由が例外の文に 1 回ずつ入っていない（$shell）: $assertOut" }
        }
    }
    $checked.Add('CAD の実行と復元の両方が失敗したときは、投げる例外の文に両方の理由を 1 回ずつ入れる（Windows PowerShell 5.1 と PowerShell 7）')

    # 子プロセスが値を変更した直後に強制終了する、CAD のタイムアウト相当の経路
    New-Fixture
    $before = New-TestSnapshot
    $marker = Join-Path $work 'child.ready'
    $childSource = @"
Set-ItemProperty -LiteralPath '$general' -Name OSMODE -Value 0
Set-ItemProperty -LiteralPath '$editor' -Name LogFilePath -Value 'C:\child\' -Type ExpandString
[IO.File]::WriteAllText('$marker', 'ready')
Start-Sleep -Seconds 300
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childSource))
    $child = Start-Process pwsh -ArgumentList (ConvertTo-NativeArgString @('-NoProfile', '-EncodedCommand', $encoded)) -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds(15)
    while (-not (Test-Path -LiteralPath $marker) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath $marker)) {
        Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue
        throw '試験の子プロセスが値を変えるところまで進まなかった'
    }
    Stop-Process -Id $child.Id -Force
    $child.WaitForExit()
    $killed = Restore-CadRegState $before -GetOtherCadProcesses $noOtherCad
    if (-not $killed.ok) { throw "強制終了のあとの復元に失敗した: $($killed.mismatch -join '; ')" }
    Assert-Original '強制終了のあと'
    $checked.Add('値を変えた子プロセスを強制終了したあとも、開始時の状態へ戻す')

    # 開始時に監視するキーそのものが無く、CAD の初回の起動で作られたときは、そのキーごと削除する
    # （親も開始時に無いので、増えたキーとして扱わないと FileDialog などが残り、戻すだけの処理でも直らない）
    $freshRoot = "HKCU:\Software\CADPeta\Tests\CadRegGuardFresh_$PID"
    if (Test-Path -LiteralPath $freshRoot) { Remove-Item -LiteralPath $freshRoot -Recurse -Force }
    $freshRecord = Join-Path $work 'record\fresh.reg.txt'
    $fresh = Get-CadRegSnapshot -Roots @($freshRoot) -AwsRoots @() -RecordPath $freshRecord
    New-Item -Path (Join-Path $freshRoot 'R26.0\FixedProfile\General Configuration') -Force | Out-Null
    New-ItemProperty -Path (Join-Path $freshRoot 'R26.0\FixedProfile\General Configuration') -Name FileDialog -Value 0 -PropertyType DWord | Out-Null
    New-ItemProperty -Path $freshRoot -Name CurVer -Value 'R26.0' -PropertyType String | Out-Null
    $freshReport = Restore-CadRegState $fresh -GetOtherCadProcesses $noOtherCad
    $freshLeft = Test-Path -LiteralPath $freshRoot
    if ($freshLeft) { Remove-Item -LiteralPath $freshRoot -Recurse -Force }
    if (-not $freshReport.ok) { throw "開始時に無かった監視のキーを戻せなかった: $($freshReport.mismatch -join '; ')" }
    if ($freshLeft) { throw '開始時に無かった監視のキーが残った' }
    if (Test-Path -LiteralPath $freshRecord) { throw '戻して一致したのに記録が残った（開始時に無かった監視のキー）' }
    $checked.Add('開始時に無かった監視のキーが試験中に作られたときは、そのキーごと削除して記録を消す')

    # インストール先（CADPeta.bundle など）は戻さず、試験の前後でファイルの名前と内容の SHA256 が同じことだけを確かめる。
    # 更新時刻とサイズは比べない（利用者の AutoCAD やほかのセッションが bundle を移しただけでも変わる）。
    # インストール先の違いはレジストリの復元と関係しないので、レジストリが戻れば記録は消す
    New-Fixture
    $install = Join-Path $work 'install\CADPeta.bundle'
    $installDll = Join-Path $install 'Contents\net10\CadPeta.Plugin.Acad.dll'
    $installRecord = Join-Path $work 'record\install.reg.txt'
    function Test-InstallChange([scriptblock]$change) {
        if (Test-Path -LiteralPath $install) { Remove-Item -LiteralPath $install -Recurse -Force }
        New-Item -ItemType Directory -Force (Split-Path -Parent $installDll) | Out-Null
        [IO.File]::WriteAllText($installDll, 'installed-a')
        [IO.File]::WriteAllText((Join-Path $install 'PackageContents.xml'), 'manifest')
        $snapshot = Get-CadRegSnapshot -Roots @($testRoot) -AwsRoots @() -RecordPath $installRecord -InstallRoots @($install)
        & $change
        $result = Restore-CadRegState $snapshot -GetOtherCadProcesses $noOtherCad
        if (Test-Path -LiteralPath $installRecord) { throw 'レジストリが戻ったのに記録が残った（インストール先の比較）' }
        return $result
    }
    $touched = Test-InstallChange {
        $past = (Get-Date).AddDays(-3)
        (Get-Item -LiteralPath $installDll).LastWriteTime = $past
        (Get-Item -LiteralPath $install).LastWriteTime = $past
    }
    if (-not $touched.ok) { throw "更新時刻が変わっただけで失敗にした: $($touched.mismatch -join '; ')" }
    # 残ったフォルダ(中身の無いフォルダを含む)は失敗にする(コピーで作ったフォルダの消し忘れを見つける)
    $leftDir = Test-InstallChange { New-Item -ItemType Directory -Force (Join-Path $install 'Contents\left\empty') | Out-Null }
    if ($leftDir.ok -or -not @($leftDir.mismatch | Where-Object { $_ -like '*left*' }).Count) { throw "インストール先に残ったフォルダを失敗にしなかった: $($leftDir.mismatch -join '; ')" }
    foreach ($case in @(
            @{ Label = '同じサイズで内容が変わった'; Path = $installDll; Change = { [IO.File]::WriteAllText($installDll, 'installed-b') } },
            @{ Label = 'ファイルが増えた'; Path = (Join-Path $install 'Contents\net10\extra.dll'); Change = { [IO.File]::WriteAllText((Join-Path $install 'Contents\net10\extra.dll'), 'x') } },
            @{ Label = 'ファイルが無くなった'; Path = (Join-Path $install 'PackageContents.xml'); Change = { Remove-Item -LiteralPath (Join-Path $install 'PackageContents.xml') } })) {
        $changed = Test-InstallChange $case.Change
        if ($changed.ok) { throw "インストール先で$($case.Label)のに成功を返した" }
        if (-not @($changed.mismatch | Where-Object { $_ -like "*$($case.Path)*" }).Count) { throw "インストール先で$($case.Label)のに、そのファイルを失敗に含めなかった:$($changed.mismatch -join '; ')" }
    }
    $checked.Add('インストール先はファイルの名前と内容の SHA256 で前後を比べ、更新時刻とフォルダの違いは無視し、内容の変更・増えたファイル・無くなったファイルを失敗にする')

    # 読めないファイル(ほかのプログラムが共有を許さずに開いている)は、ハッシュの代わりの値で比べない。
    # 前の記録で読めなければ記録を書かずに止まり(環境を変える前に止まる)、後の記録で読めなければ照合の失敗にする。
    # 前後とも同じ理由で読めないときに一致と判定しないことも確かめる
    if (Test-Path -LiteralPath $install) { Remove-Item -LiteralPath $install -Recurse -Force }
    New-Item -ItemType Directory -Force (Split-Path -Parent $installDll) | Out-Null
    [IO.File]::WriteAllText($installDll, 'installed-a')
    $aws = Join-Path $awsRoot 'FixedProfile.aws'
    foreach ($locked in @($installDll, $aws)) {
        $stream = [IO.FileStream]::new($locked, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        try {
            $threw = $false
            try { [void](Get-CadRegSnapshot -Roots @($testRoot) -AwsRoots @($awsRoot) -RecordPath $installRecord -InstallRoots @($install)) } catch { $threw = $true }
        }
        finally { $stream.Dispose() }
        if (-not $threw) { throw "前の記録で読めないファイルがあるのに記録した: $locked" }
        if (Test-Path -LiteralPath $installRecord) { throw "前の記録で読めないファイルがあるのに記録のファイルを書いた: $locked" }
        $snapshot = Get-CadRegSnapshot -Roots @($testRoot) -AwsRoots @($awsRoot) -RecordPath $installRecord -InstallRoots @($install)
        $stream = [IO.FileStream]::new($locked, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        try { $result = Restore-CadRegState $snapshot -GetOtherCadProcesses $noOtherCad }
        finally { $stream.Dispose() }
        if ($result.ok) { throw "後の記録で読めないファイルがあるのに一致と判定した: $locked" }
        if (Test-Path -LiteralPath $installRecord) { [void](Restore-CadRegRecord $installRecord -GetOtherCadProcesses $noOtherCad) }
    }
    $checked.Add('読めないファイル(インストール先と .aws)は、前の記録なら記録を書かずに止まり、後の記録なら照合の失敗にする')

    # 信頼済みのフォルダへのコピー: 試験する DLL のフォルダ(依存の DLL と lang を含む)を DLL の信頼済みのフォルダの直下へ、
    # LISP を LISP の信頼済みのフォルダの直下へコピーし、戻すときはコピーしたものだけを削除する(元からあるファイルは残す)
    New-Fixture
    $trustedDotnet = Join-Path $work 'trusted\dotnet'
    $trustedLisp = Join-Path $work 'trusted\lisp'
    $build = Join-Path $work 'build\net8.0-windows'
    New-Item -ItemType Directory -Force $trustedDotnet, $trustedLisp, (Join-Path $build 'lang') | Out-Null
    [IO.File]::WriteAllText((Join-Path $trustedDotnet 'Foreign.dll'), 'foreign')
    [IO.File]::WriteAllText((Join-Path $trustedLisp 'foreign.lsp'), 'foreign lisp')
    $pluginDll = Join-Path $build 'CadPeta.Plugin.Acad.dll'
    [IO.File]::WriteAllText($pluginDll, 'plugin')
    [IO.File]::WriteAllText((Join-Path $build 'CadPeta.Core.dll'), 'core')
    [IO.File]::WriteAllText((Join-Path $build 'lang\1041.lang'), 'ja')
    $lsp = Join-Path $work 'build\make.lsp'
    [IO.File]::WriteAllText($lsp, '(princ)')
    $stageRecord = Join-Path $work 'record\stage.reg.txt'
    $trustedSetting = "C:\Other;$($trustedDotnet.ToUpperInvariant())\;$trustedLisp"
    function New-StagedSnapshot([string]$TrustedPaths = $trustedSetting) {
        Get-CadRegSnapshot -Roots @($testRoot) -AwsRoots @() -RecordPath $stageRecord -InstallRoots @() `
            -StageToTrusted @($pluginDll, $lsp) -TrustedDotnetFolder $trustedDotnet -TrustedLispFolder $trustedLisp -TrustedPaths $TrustedPaths
    }
    $foreignBefore = (Format-FolderListing (Get-FolderListing $trustedDotnet)) + "`n|`n" + (Format-FolderListing (Get-FolderListing $trustedLisp))
    $staged = New-StagedSnapshot
    foreach ($copied in @('CadPeta.Plugin.Acad.dll', 'CadPeta.Core.dll', 'lang\1041.lang')) {
        if (-not (Test-Path -LiteralPath (Join-Path $trustedDotnet $copied))) { throw "信頼済みのフォルダへコピーしていない: $copied" }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $trustedLisp 'make.lsp'))) { throw '信頼済みのフォルダへ LISP をコピーしていない' }
    $restored = Restore-CadRegState $staged -GetOtherCadProcesses $noOtherCad
    if (-not $restored.ok) { throw "コピーを戻した後の照合が失敗した: $($restored.mismatch -join '; ')" }
    $after = (Format-FolderListing (Get-FolderListing $trustedDotnet)) + "`n|`n" + (Format-FolderListing (Get-FolderListing $trustedLisp))
    if ($after -ne $foreignBefore) { throw "コピーしたものが残ったか、元からあるファイルが変わった: $after" }
    if (Test-Path -LiteralPath (Join-Path $trustedDotnet 'lang')) { throw 'コピーで作ったフォルダが残った' }
    $checked.Add('信頼済みのフォルダの直下へ DLL のフォルダと LISP をコピーし、戻すときはコピーしたものだけを削除して元からあるファイルを残す')

    # 同じ名前のファイルがあれば、何もコピーせず、記録も書かずに止まる
    [IO.File]::WriteAllText((Join-Path $trustedDotnet 'CadPeta.Core.dll'), 'someone else')
    $threw = $false
    try { [void](New-StagedSnapshot) } catch { $threw = $true }
    if (-not $threw) { throw '信頼済みのフォルダに同じ名前のファイルがあるのに止まらなかった' }
    if (Test-Path -LiteralPath (Join-Path $trustedDotnet 'CadPeta.Plugin.Acad.dll')) { throw '同じ名前のファイルがあるのにほかのファイルをコピーした' }
    if ([IO.File]::ReadAllText((Join-Path $trustedDotnet 'CadPeta.Core.dll')) -ne 'someone else') { throw '同じ名前の元からあるファイルを上書きした' }
    if (Test-Path -LiteralPath $stageRecord) { throw '同じ名前のファイルがあるのに記録のファイルを書いた' }
    Remove-Item -LiteralPath (Join-Path $trustedDotnet 'CadPeta.Core.dll')
    $checked.Add('信頼済みのフォルダに同じ名前のファイルがあれば、何もコピーせず記録も書かずに止まる')

    # プロファイルの TRUSTEDPATHS に信頼済みのフォルダが無ければ、何も変えずに止まる。末尾の \、大文字と小文字の違い、
    # 親のフォルダの「\...」(下のフォルダも信頼する)は、信頼されているとみなす
    $threw = $false
    try { [void](New-StagedSnapshot "C:\Other;$trustedLisp") } catch { $threw = $true }
    if (-not $threw -or (Test-Path -LiteralPath (Join-Path $trustedDotnet 'CadPeta.Plugin.Acad.dll')) -or (Test-Path -LiteralPath $stageRecord)) {
        throw 'TRUSTEDPATHS に信頼済みのフォルダが無いのに止まらなかったか、何かを変えた'
    }
    $staged = New-StagedSnapshot "$(Split-Path -Parent $trustedDotnet)\..."
    $restored = Restore-CadRegState $staged -GetOtherCadProcesses $noOtherCad
    if (-not $restored.ok) { throw "親のフォルダの \... の TRUSTEDPATHS で照合が失敗した: $($restored.mismatch -join '; ')" }
    $checked.Add('TRUSTEDPATHS に信頼済みのフォルダが無ければ何も変えずに止まり、末尾の \・大文字と小文字・親の \... を扱う')

    # 中断した試験の後始末: 戻すだけの処理(記録のファイルから戻す)も、コピーしたものだけを削除する
    $staged = New-StagedSnapshot
    $restoreOnly = Restore-CadRegRecord $stageRecord -GetOtherCadProcesses $noOtherCad
    if (-not $restoreOnly.ok) { throw "戻すだけの処理が失敗した: $($restoreOnly.mismatch -join '; ')" }
    if (Test-Path -LiteralPath (Join-Path $trustedDotnet 'CadPeta.Plugin.Acad.dll')) { throw '戻すだけの処理がコピーを削除しなかった' }
    if ([IO.File]::ReadAllText((Join-Path $trustedDotnet 'Foreign.dll')) -ne 'foreign') { throw '戻すだけの処理が元からあるファイルを変えた' }
    $checked.Add('戻すだけの処理も、記録したコピーだけを削除する')

    # コピーで作ったフォルダを列挙・削除できなくても(アクセス拒否)、失敗を照合の結果に入れて、レジストリの復元を続ける
    New-Fixture
    New-Item -ItemType Directory -Force $trustedDotnet, $trustedLisp, (Join-Path $build 'lang') | Out-Null
    [IO.File]::WriteAllText($pluginDll, 'plugin'); [IO.File]::WriteAllText((Join-Path $build 'lang\1041.lang'), 'ja'); [IO.File]::WriteAllText($lsp, '(princ)')
    $staged = New-StagedSnapshot
    Set-Pollution
    $langDir = Join-Path $trustedDotnet 'lang'
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    $denyList = New-Object System.Security.AccessControl.FileSystemAccessRule($me, [System.Security.AccessControl.FileSystemRights]::ListDirectory, [System.Security.AccessControl.AccessControlType]::Deny)
    $acl = Get-Acl -LiteralPath $langDir; $acl.AddAccessRule($denyList); Set-Acl -LiteralPath $langDir -AclObject $acl
    try { $denied = Restore-CadRegState $staged -GetOtherCadProcesses $noOtherCad }
    finally { $acl = Get-Acl -LiteralPath $langDir; [void]$acl.RemoveAccessRule($denyList); Set-Acl -LiteralPath $langDir -AclObject $acl }
    if ($denied.ok) { throw 'コピーで作ったフォルダを削除できなかったのに成功を返した' }
    if (-not @($denied.mismatch | Where-Object { $_ -like "*$langDir*" }).Count) { throw "削除できなかったフォルダを照合の結果に入れなかった: $($denied.mismatch -join '; ')" }
    # この記録は .aws を対象にしていない(-AwsRoots @())ので、レジストリの値が戻ったことを確かめる
    $g = Get-Item -LiteralPath $general
    if ($g.GetValue('OSMODE') -ne 4133 -or $g.GetValue('FileDialog') -ne 0 -or $null -ne $g.GetValue('SECURELOAD')) {
        throw "コピーで作ったフォルダを削除できないときに、レジストリを戻さなかった: OSMODE=$($g.GetValue('OSMODE')) FileDialog=$($g.GetValue('FileDialog'))"
    }
    if (Test-Path -LiteralPath $langDir) { Remove-Item -LiteralPath $langDir -Recurse -Force }
    if (Test-Path -LiteralPath $stageRecord) { Remove-Item -LiteralPath $stageRecord -Force }
    # 戻し切れなかったので、環境を変えた印が残っている(利用者が手で片付けた後と同じにする)
    Clear-RecordEnvironmentChanged
    $checked.Add('コピーで作ったフォルダを削除できなくても、失敗を結果に入れてレジストリの復元を続ける')

    # AutoCAD の版は、起動する AutoCAD の ProgID から決める(テンプレートのパスからは決めない)
    if ((Get-AcadVersionFromProgId 'AutoCAD.Application.25.1') -ne '2026' -or (Get-AcadVersionFromProgId 'AutoCAD.Application.24.3') -ne '2024' -or
        (Get-AcadVersionFromProgId 'AutoCAD.Application.26.0') -ne '2027') { throw 'ProgID から AutoCAD の版を決められない' }
    $threw = $false
    try { [void](Get-AcadVersionFromProgId 'AutoCAD.Application') } catch { $threw = $true }
    if (-not $threw) { throw '版の分からない ProgID で止まらなかった' }
    $checked.Add('AutoCAD の版を ProgID から決め、分からなければ止まる')

    # 往復: 記録して何も変えずに戻すと、戻す値も違いも無い(インストール先・信頼済みのフォルダへのコピー・.aws を含む)
    New-Fixture
    New-Item -ItemType Directory -Force $trustedDotnet, $trustedLisp, (Join-Path $build 'lang') | Out-Null
    [IO.File]::WriteAllText($pluginDll, 'plugin'); [IO.File]::WriteAllText((Join-Path $build 'lang\1041.lang'), 'ja'); [IO.File]::WriteAllText($lsp, '(princ)')
    $roundTrip = Get-CadRegSnapshot -Roots @($testRoot) -AwsRoots @($awsRoot) -RecordPath $stageRecord -InstallRoots @($install) `
        -StageToTrusted @($pluginDll, $lsp) -TrustedDotnetFolder $trustedDotnet -TrustedLispFolder $trustedLisp -TrustedPaths $trustedSetting
    $roundReport = Restore-CadRegState $roundTrip -GetOtherCadProcesses $noOtherCad
    if (-not $roundReport.ok -or @($roundReport.actions | Where-Object { $_ -notlike '信頼済みのフォルダへのコピーを削除*' }).Count) {
        throw "記録して何も変えずに戻したのに、戻した値か違いがある: $($roundReport.actions -join '; ') / $($roundReport.mismatch -join '; ')"
    }
    $checked.Add('往復: 記録して何も変えずに戻すと、戻す値も違いも無い')

    # 実行前の記録は、環境を変える前の 1 か所で取る。環境を変えた後(戻す前)に実行前の記録を取ろうとしたら止める
    New-Fixture
    $first = Get-CadRegSnapshot -Roots @($testRoot) -AwsRoots @($awsRoot) -RecordPath $stageRecord -InstallRoots @()
    $threw = $false
    try { [void](Get-CadRegSnapshot -Roots @($testRoot) -AwsRoots @($awsRoot) -RecordPath (Join-Path $work 'record\second.reg.txt') -InstallRoots @()) } catch { $threw = $true }
    if (-not $threw) { throw '環境を変えた後(戻す前)に、別の記録のファイルで実行前の記録を取れてしまった' }
    if (Test-Path -LiteralPath (Join-Path $work 'record\second.reg.txt')) { throw '環境を変えた後の記録のファイルを書いた' }
    $report = Restore-CadRegState $first -GetOtherCadProcesses $noOtherCad
    if (-not $report.ok) { throw "戻せなかった: $($report.mismatch -join '; ')" }
    $again = Get-CadRegSnapshot -Roots @($testRoot) -AwsRoots @($awsRoot) -RecordPath $stageRecord -InstallRoots @()
    [void](Restore-CadRegState $again -GetOtherCadProcesses $noOtherCad)
    $checked.Add('環境を変えた後(戻す前)に実行前の記録を取ろうとしたら止め、戻した後は取れる')
    # 自分の起動したプロセスが終了の直前に起動した子プロセス（acad.exe 2024・2026 が終了時に起動する accoreconsole）は、
    # 上限まで終了を待ち、上限を過ぎても残る子プロセスだけを返す
    $parentSource = "Start-Process pwsh -ArgumentList '-NoProfile -Command Start-Sleep -Seconds 3' -WindowStyle Hidden"
    $parent = Start-Process pwsh -ArgumentList (ConvertTo-NativeArgString @('-NoProfile', '-EncodedCommand', [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($parentSource)))) -PassThru -WindowStyle Hidden
    $parent.WaitForExit()
    $short = @(Wait-CadRegChildProcesses $parent.Id 1)
    if ($short.Count -ne 1) { throw "上限を過ぎても残る子プロセスを返さなかった: $($short.Count) 個" }
    $long = @(Wait-CadRegChildProcesses $parent.Id 30)
    if ($long.Count -ne 0) { throw "終了した子プロセスを残っているとした: $($long.Count) 個" }
    $checked.Add('自分の起動したプロセスの子プロセスは上限まで終了を待ち、上限を過ぎても残るものだけを返す')

    "CadRegGuard自己試験 OK（確かめた約束 $($checked.Count) 件）"
    $checked | ForEach-Object { "  - $_" }
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
