# 互換 CAD の試験が途中で止まり、自動読み込みの登録を戻せなかったときに、バックアップから戻す。
# ARES 2024 以降はレジストリの登録、2023 以前は profile.xml のスタートアップの登録を戻す。
# 使い方: powershell -File tests\tools\common\compat_autoload_restore.ps1 -Product Ares2027 [-WhatIf]（-WhatIf は戻すものを表示するだけ）
# 自動で実行するときは、powershell -Command "& 'tests\tools\common\compat_autoload_restore.ps1' -Product Ares2027 -Confirm:`$false"
# （Windows PowerShell 5.1 の -File は -Confirm:$false を受け付けない）
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param([Parameter(Mandatory)][string]$Product)
$ErrorActionPreference = 'Stop'
$evidenceLog = Join-Path $env:TEMP "compat_autoload_restore_$PID.log"
$PollMilliseconds = 100
. "$PSScriptRoot\compat_cad_com.ps1"

$key = Get-CompatAutoloadKey $Product
$backup = Get-CompatAutoloadBackupFile $Product
$profileBackup = Get-CompatProfileBackupDir $Product
$targets = @()
if ($null -ne $key -and (Test-Path -LiteralPath $backup)) { $targets += "レジストリの登録 $key（バックアップ: $backup）" }
if (Test-Path -LiteralPath $profileBackup) { $targets += "profile.xml のスタートアップの登録（$profileBackup）" }
if ($targets.Count -eq 0) { "戻すバックアップはありません: $Product"; return }
$changePlan = New-ChangePlan "$Product の自動読み込みの登録" "バックアップから戻す: $Product" $targets
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)

# バックアップを作るのは ARES だけなので、終了を確かめる CAD は ARES の実行ファイル。動いていれば何も戻さずに止まり、バックアップを残す
$year = Get-CompatProfileAutoloadYear $Product
$exe = if ($year) { Get-AresExe $year } else { $null }
$errors = @(Restore-CompatProductBackups $key $backup $profileBackup $exe)
if ($errors.Count -gt 0) { throw ($errors -join '; ') }
"自動読み込みの登録を戻しました: $($targets -join '、')"
