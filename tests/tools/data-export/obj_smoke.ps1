# PETADATAスモーク: TESTCHART2.dwgをaccoreconsoleで開き、全選択→尺度Enterで構造SVGを出力する。
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$Ver = "2026",
    [ValidateSet("PETADATA")][string]$Cmd = "PETADATA",
    [string]$OutDir = "$env:TEMP\cadpeta_p2a\obj_smoke",
    [string]$SelectResponse = "ALL"   # GetSelection への応答（ALL / W 等）
)
$ErrorActionPreference = "Stop"
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\common\test_helpers.ps1"
$changePlan = New-AcadE2ePlan 'obj_smoke.ps1' $OutDir
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)
$repo = "C:\Users\wk\repo\CADPeta"
$tfm = switch ($Ver) { "2024" { "net48" } "2026" { "net8.0-windows" } "2027" { "net10.0-windows" } }
$acad = "C:\Program Files\Autodesk\AutoCAD $Ver\accoreconsole.exe"
. "$PSScriptRoot\..\common\test_helpers.ps1"
$plugin = (Get-E2ePluginPath Acad $tfm)
New-Item -ItemType Directory -Force $OutDir | Out-Null
Remove-Item "$OutDir\*" -Recurse -Force -ErrorAction SilentlyContinue
$dwg = Join-Path $OutDir "TESTCHART2.dwg"
Copy-Item "$repo\tests\assets\testcharts\TESTCHART2.dwg" $dwg

# PETADATA → 選択（応答＋空行で確定）→ 尺度 Enter（空行）
$scr = Join-Path $OutDir "run.scr"
@"
$((Get-AccoreNetloadLines $plugin) -join "`r`n")
$Cmd
$SelectResponse


_.QUIT _Y
"@ | Out-File $scr -Encoding ascii

$stdout = Join-Path $OutDir "accore_stdout.log"
# CADが強制終了しても永続sysvar（SECURELOAD等）を利用者プロファイルへ残さないレジストリ保全ガード
. "$PSScriptRoot\..\regression\CadRegGuard.ps1"
$cadExitCode = $null # [ref] で受け取る変数を先に作る
Invoke-AcadConsoleGuarded $acad $dwg $scr $stdout ([ref]$cadExitCode) -StageToTrusted @($plugin)
"exit=$cadExitCode"
Get-ChildItem $OutDir -Filter *.svg -Recurse | ForEach-Object { "{0}  {1:N0} bytes" -f $_.Name, $_.Length }
