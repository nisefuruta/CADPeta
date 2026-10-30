# run_case.ps1: 1ケース分を「別プロセス」として起動し、タイムアウト監視する外側ラッパー。
# ungroup_case.ps1 自身も Quit() 失敗時の強制終了を持つが、それは Ungroup() 呼び出しが
# 例外で返ってきた後の話であり、Ungroup() 自体が戻ってこない（真のハング）場合は
# ungroup_case.ps1 のプロセスごと固まる。本スクリプトはその外側でプロセス単位の
# ウォッチドッグを掛け、タイムアウト時は ungroup_case.ps1 が書き出す PidFile を読んで
# Excel の実PIDを名指しで強制終了し、ケース実行プロセス自体も強制終了する。
# 実測（2026-07-10, N=50,000）では Ungroup 失敗は例外で数秒〜十数秒以内に返っており、
# ハングは未観測。本スクリプトは「観測されていない事象への保険」として備える。
param(
    [Parameter(Mandatory=$true)][string]$Emf,
    [Parameter(Mandatory=$true)][string]$OutJson,
    [string]$Route = "clipboard",
    [int]$TimeoutSec = 120,
    [switch]$Visible
)
. "$PSScriptRoot\..\..\common\threshold_case.ps1"
$caseScript = Join-Path $PSScriptRoot "ungroup_case.ps1"
$extraArguments = if ($Visible) { @("-Visible") } else { @() }
return Invoke-UngroupCaseProcess $caseScript $Emf $OutJson $Route $TimeoutSec -ExtraArguments $extraArguments
