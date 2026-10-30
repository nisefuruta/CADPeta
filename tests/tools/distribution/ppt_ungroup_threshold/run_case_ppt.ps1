# run_case_ppt.ps1: 1ケースを別プロセスで起動しタイムアウト監視する外側ラッパー。
# tests/tools/cad-typography/emf_ungroup_threshold/run_case.ps1（Excel 版）の PowerPoint 移植。
# タイムアウト時は ungroup_case_ppt.ps1 が書く PidFile を読んで PowerPoint 実PIDを名指しで
# 強制終了し、ケース実行プロセスも強制終了する。
param(
    [Parameter(Mandatory=$true)][string]$Emf,
    [Parameter(Mandatory=$true)][string]$OutJson,
    [string]$Route = "clipboard",
    [int]$TimeoutSec = 120
)
. "$PSScriptRoot\..\..\common\threshold_case.ps1"
$caseScript = Join-Path $PSScriptRoot "ungroup_case_ppt.ps1"
return Invoke-UngroupCaseProcess $caseScript $Emf $OutJson $Route $TimeoutSec
