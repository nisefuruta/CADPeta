[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$Ver = "2026",
    [string]$Repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")).Path,
    [string]$Dwg = "C:\Users\wk\AppData\Local\Temp\cadpeta_p3b_ltsym\test_chart.dwg",
    [string]$OutDir = "C:\Users\wk\AppData\Local\Temp\cadpeta_p3b_ltsym\extract"
)
$ErrorActionPreference = "Stop"
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\..\common\test_helpers.ps1"
$changePlan = New-AcadE2ePlan 'run_peta_extract.ps1' $OutDir
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)
$tfm = switch ($Ver) { "2024" { "net48" } "2026" { "net8.0-windows" } "2027" { "net10.0-windows" } }
$acad = "C:\Program Files\Autodesk\AutoCAD $Ver\accoreconsole.exe"
. "$PSScriptRoot\..\..\common\test_helpers.ps1"
$plugin = (Get-E2ePluginPath Acad $tfm)
New-Item -ItemType Directory -Force $OutDir | Out-Null
Remove-Item "$OutDir\*" -Recurse -Force -ErrorAction SilentlyContinue
$dwgCopy = Join-Path $OutDir "test_chart.dwg"
Copy-Item $Dwg $dwgCopy

$scr = Join-Path $OutDir "run.scr"
@"
$((Get-AccoreNetloadLines $plugin) -join "`r`n")
PETADATA
ALL


_.QUIT _Y
"@ | Out-File $scr -Encoding ascii

$stdout = Join-Path $OutDir "accore_stdout.log"
# CAD強制終了でも永続sysvar（SECURELOAD等）を復元するため、実行全体をレジストリガードで包む
. "$PSScriptRoot\..\..\regression\CadRegGuard.ps1"
$regGuard = Get-CadRegSnapshot -StageToTrusted @($plugin) -AcadVersion $Ver
$runError = $null
$regReport = $null # [ref] で受け取る変数を先に作る
try {
    Push-Location -LiteralPath (Split-Path -Parent $scr)  # accoreconsole は図面ごとのログを作業ディレクトリへ書く
    try { & $acad /i $dwgCopy /s $scr /l ja-JP 2>&1 | Out-File $stdout -Encoding utf8 } finally { Pop-Location }
    $cadExitCode = $LASTEXITCODE
}
catch { $runError = $_ }
finally {
    Restore-CadRegStateAndReport $regGuard ([ref]$regReport)
}
Assert-CadRunAndRegRestore $runError $regReport
"exit=$cadExitCode"
Get-ChildItem $OutDir -Filter *.svg -Recurse | ForEach-Object { "{0}  {1:N0} bytes" -f $_.Name, $_.Length }
