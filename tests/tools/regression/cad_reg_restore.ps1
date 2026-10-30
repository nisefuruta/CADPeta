# 試験が途中で止まり、AutoCAD のプロファイル（レジストリと .aws）を戻せなかったときに、記録のファイルから戻す。
# ほかの AutoCAD が動いていれば戻さない。すべて終了してから実行する。
# 使い方: pwsh -File tests\tools\regression\cad_reg_restore.ps1 [-WhatIf]（-WhatIf は戻す違いを表示するだけ）
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param([string]$RecordPath = '')
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\CadRegGuard.ps1"
if ([string]::IsNullOrEmpty($RecordPath)) { $RecordPath = $script:CadRegGuardDefaultRecord }
if (-not (Test-Path -LiteralPath $RecordPath)) { "戻す記録はありません: $RecordPath"; return }
$record = Read-CadRegRecord $RecordPath
$differences = @(Compare-CadRegState $record (Read-CadRegState $record.Roots)) + @(Compare-RecordBaseline $record.Baseline @('Restore'))
$changePlan = New-ChangePlan 'AutoCAD のプロファイル（レジストリと .aws）' "記録から戻す: $RecordPath" (
    @("記録と今の違い: $($differences.Count) 件") + @($differences | ForEach-Object { "  $_" }))
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)
$report = Restore-CadRegRecord $RecordPath
"戻した内容: $($report.actions.Count) 件"
if (-not $report.ok) { throw ($report.mismatch -join '; ') }
"AutoCAD のプロファイルを記録の状態へ戻しました: $RecordPath"
