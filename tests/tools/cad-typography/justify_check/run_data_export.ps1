# PETADATA: justify_chart.dwgをaccoreconsoleで開き、全選択→尺度Enter（1:1）で構造SVGを取得する。
# 使い方: pwsh -File run_data_export.ps1 [-OutDir <出力先>]
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$OutDir = "$env:TEMP\cadpeta_p3b_justify\data-export"
)
$ErrorActionPreference = "Stop"
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\..\common\test_helpers.ps1"
$changePlan = New-AcadE2ePlan 'run_data_export.ps1' $OutDir
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)
$repo = "C:\Users\wk\repo\CADPeta"
$acad = "C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe"
. "$PSScriptRoot\..\..\common\test_helpers.ps1"
$plugin = (Get-E2ePluginPath Acad 'net8.0-windows')
$stray = Get-Process AcCoreConsole -ErrorAction SilentlyContinue
if ($stray) { throw "accoreconsole の残留プロセスがある（PID: $($stray.Id -join ',')）。先に確認すること" }

New-Item -ItemType Directory -Force $OutDir | Out-Null
Remove-Item "$OutDir\*" -Recurse -Force -ErrorAction SilentlyContinue
$dwg = Join-Path $OutDir "justify_chart.dwg"
Copy-Item "$repo\tests\tools\cad-typography\justify_check\justify_chart.dwg" $dwg

# PETADATA → ALL（＋空行で選択確定）→ 尺度Enter（空行、1:1）
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
$cadExitCode = $null # [ref] で受け取る変数を先に作る
Invoke-AcadConsoleGuarded $acad $dwg $scr $stdout ([ref]$cadExitCode) -StageToTrusted @($plugin)
"exit=$cadExitCode"
Get-ChildItem $OutDir -Filter *.svg -Recurse | ForEach-Object { "{0}  {1:N0} bytes" -f $_.Name, $_.Length }
