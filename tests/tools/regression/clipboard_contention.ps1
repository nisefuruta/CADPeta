# 別プロセスによる実OpenClipboard競合で、CLIの失敗契約と解放後の回復を検査する。
[CmdletBinding()]
param(
    [string]$Configuration = 'Debug',
    [string]$InputPdf,
    [string]$OutRoot = "$env:TEMP\CadPeta_clipboard_contention",
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\common\test_helpers.ps1"   # ConvertTo-NativeArgString（Start-Process の引数を引用符で囲む）
[Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)
$cp932 = [Text.Encoding]::GetEncoding(932)
function Read-CliText([string]$path) { $cp932.GetString([IO.File]::ReadAllBytes($path)) }
$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
if (-not $InputPdf) { $InputPdf = Join-Path $repo 'tests\assets\pdf-fixtures\bg_rect_fixture.pdf' }
$cli = Join-Path $repo "src\CadPeta.Cli\bin\$Configuration\net10.0-windows\win-x64\publish\cadpeta.exe"
if (-not (Test-Path -LiteralPath $cli)) {
    $cli = Join-Path $repo "src\CadPeta.Cli\bin\$Configuration\net10.0-windows\cadpeta.exe"
}
foreach ($required in @($cli, $InputPdf)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "必須ファイルがない: $required" }
}

$root = [IO.Path]::GetFullPath($OutRoot)
New-Item -ItemType Directory -Force -Path $root | Out-Null
$ready = Join-Path $root 'holder.ready.json'
$release = Join-Path $root 'holder.release'
$holderOut = Join-Path $root 'holder.stdout.log'
$holderErr = Join-Path $root 'holder.stderr.log'
$emf = Join-Path $root 'contention.emf'
$stdout = Join-Path $root 'cli.stdout.log'
$stderr = Join-Path $root 'cli.stderr.log'
Remove-Item -LiteralPath $ready, $release, $emf, $stdout, $stderr, $holderOut, $holderErr -Force -ErrorAction SilentlyContinue

Add-Type -AssemblyName System.Windows.Forms
$sentinel = "CADPeta競合試験:$([Guid]::NewGuid())"
[System.Windows.Forms.Clipboard]::SetText($sentinel)

$pwsh = Join-Path $PSHOME 'pwsh.exe'
if (-not (Test-Path -LiteralPath $pwsh)) { throw "PowerShell 7がない: $pwsh" }
$holder = Start-Process -FilePath $pwsh -ArgumentList (ConvertTo-NativeArgString @(
    '-NoProfile', '-Sta', '-File', (Join-Path $PSScriptRoot 'clipboard_holder.ps1'),
    '-ReadyPath', $ready, '-ReleasePath', $release, '-TimeoutSeconds', ($TimeoutSeconds + 10)
)) -RedirectStandardOutput $holderOut -RedirectStandardError $holderErr -PassThru -WindowStyle Hidden
try {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not (Test-Path -LiteralPath $ready) -and -not $holder.HasExited -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 50
    }
    if (-not (Test-Path -LiteralPath $ready)) {
        throw "競合保持プロセスが準備完了にならなかった: $(Get-Content -LiteralPath $holderErr -Raw -ErrorAction SilentlyContinue)"
    }
    $holderInfo = Get-Content -LiteralPath $ready -Raw | ConvertFrom-Json
    if ([int]$holderInfo.pid -ne $holder.Id -or [int64]$holderInfo.hwnd -eq 0) {
        throw "保持プロセスの所有証跡が不正: PID=$($holderInfo.pid) HWND=$($holderInfo.hwnd)"
    }

    $process = Start-Process -FilePath $cli -ArgumentList (ConvertTo-NativeArgString @($InputPdf, '--emf', $emf, '--clipboard')) `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id -Force
        throw "競合中のCLIが${TimeoutSeconds}秒で終了しなかった"
    }
    if ($process.ExitCode -eq 0) { throw '競合中のCLIが成功終了した' }
    $errorText = Read-CliText $stderr
    if ($errorText -notmatch 'OpenClipboard' -or $errorText -notmatch "PID[^0-9]*$($holder.Id)\b") {
        throw "競合診断に段名または保持PIDがない: $errorText"
    }
    if (-not (Test-Path -LiteralPath $emf) -or (Get-Item -LiteralPath $emf).Length -lt 88) {
        throw '競合失敗時に完成済みEMFが保持されなかった'
    }
    [byte[]]$header = Get-Content -LiteralPath $emf -AsByteStream -TotalCount 44
    if ([BitConverter]::ToUInt32($header, 0) -ne 1 -or [BitConverter]::ToUInt32($header, 40) -ne 0x464D4520) {
        throw '競合失敗時の退避EMFが有効なEMFヘッダを持たない'
    }
}
finally {
    New-Item -ItemType File -Force -Path $release | Out-Null
    if (-not $holder.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $holder.Id -Force
        throw "競合保持プロセスが${TimeoutSeconds}秒で終了しなかった"
    }
    if ($holder.ExitCode -ne 0) {
        throw "競合保持プロセスが異常終了した: $(Get-Content -LiteralPath $holderErr -Raw -ErrorAction SilentlyContinue)"
    }
}
if ([System.Windows.Forms.Clipboard]::GetText() -ne $sentinel) {
    throw 'OpenClipboard競合中に既存クリップボード内容を変更した'
}

$recoveryOut = Join-Path $root 'recovery.stdout.log'
$recoveryErr = Join-Path $root 'recovery.stderr.log'
$recoveryEmf = Join-Path $root 'recovery.emf'
Remove-Item -LiteralPath $recoveryEmf -Force -ErrorAction SilentlyContinue
$recovery = Start-Process -FilePath $cli -ArgumentList (ConvertTo-NativeArgString @($InputPdf, '--emf', $recoveryEmf, '--clipboard')) `
    -RedirectStandardOutput $recoveryOut -RedirectStandardError $recoveryErr -PassThru -WindowStyle Hidden
if (-not $recovery.WaitForExit($TimeoutSeconds * 1000)) {
    Stop-Process -Id $recovery.Id -Force
    throw "解放後のCLIが${TimeoutSeconds}秒で終了しなかった"
}
if ($recovery.ExitCode -ne 0) {
    throw "解放後のCLIが回復しなかった: $(Get-Content -LiteralPath $recoveryErr -Raw -ErrorAction SilentlyContinue)"
}
"クリップボード競合回帰 OK: 保持PID=$($holder.Id)、競合中は非0・既存内容不変・EMF保持、解放後は成功"
