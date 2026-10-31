# 互換 CAD の COM の試験が、自分が起動した CAD の異常終了を失敗にすること（compat_cad_com.ps1 の
# Assert-ConnectedExecutable と Stop-OwnedCadProcess）を、CAD の代わりに cmd.exe の終了コードで確かめる。
# CAD は Quit の後の終了の処理で異常終了しても、Quit の呼び出しは成功を返すので、終了コードを見ないと試験が合格になる。
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$evidenceLog = Join-Path $env:TEMP "compat_process_exit_selftest_$PID.log"
$PollMilliseconds = 100
. "$PSScriptRoot\compat_cad_com.ps1"

$cmd = [IO.Path]::GetFullPath((Join-Path $env:SystemRoot 'System32\cmd.exe'))
$checked = New-Object System.Collections.Generic.List[string]
# アクセス違反の終了コード（0xC0000005）。ARES は終了の処理で DBObject のファイナライザがこれで終了した
$accessViolation = -1073741819
# AutoCAD 2027 が正常に終了したときの終了コードの 1 つ（0xD2E2BABE。NTSTATUS の重大度はエラーの形をしている）
$acadNormalExit = -756892994

# 2 秒動いてから $exitCode で終了する cmd.exe を起動し、試験と同じ手順で接続先として確かめた Process を返す
function Start-FakeCad([int]$exitCode) {
  $started = Start-Process -FilePath $cmd -ArgumentList "/c ping -n 3 127.0.0.1 >nul & exit $exitCode" -PassThru -WindowStyle Hidden
  return Assert-ConnectedExecutable $started.Id $cmd
}

try {
  # 1. 正常な終了の終了コードが分かっている製品（ARES の 0）で、その値で終了したとき: 失敗にしない
  $owned = Start-FakeCad 0
  $failures = @()
  Stop-OwnedCadProcess $owned 30 ([ref]$failures) -QuitExitCode 0
  if ($failures.Count -ne 0) { throw "1: 正常終了を失敗にした: $($failures -join '; ')" }
  $checked.Add('正常な終了の終了コードで終了した CAD は失敗にしない')

  # 2. 終了の待ちの途中で異常終了したとき: 終了コードを付けて失敗にする
  $owned = Start-FakeCad $accessViolation
  $failures = @()
  Stop-OwnedCadProcess $owned 30 ([ref]$failures) -QuitExitCode 0
  if (-not @($failures | Where-Object { $_ -match '0xC0000005' }).Count) { throw "2: 異常終了を失敗にしなかった: $($failures -join '; ')" }
  $checked.Add('終了の待ちの途中で異常終了した CAD は、終了コードを付けて失敗にする')

  # 3. 終了の待ちを始める前に異常終了していたとき（Quit の後の後始末の間に終了した）: 同じく失敗にする
  $owned = Start-FakeCad $accessViolation
  [void](Get-Process -Id $owned.Id).WaitForExit(30000)
  $failures = @()
  Stop-OwnedCadProcess $owned 30 ([ref]$failures) -QuitExitCode 0
  if (-not @($failures | Where-Object { $_ -match '0xC0000005' }).Count) { throw "3: 待ちの前の異常終了を失敗にしなかった: $($failures -join '; ')" }
  $checked.Add('終了の待ちを始める前に異常終了した CAD も、終了コードを付けて失敗にする')

  # 4. 正常な終了の終了コードが決まらない製品（AutoCAD は正常に終了しても 0xBEBEBABE・0xD2E2BABE のような値を返す。2026-10-31 実測）: 失敗にしない
  $owned = Start-FakeCad $acadNormalExit
  $failures = @()
  Stop-OwnedCadProcess $owned 30 ([ref]$failures) -QuitExitCode $null
  if ($failures.Count -ne 0) { throw "4: 終了コードの決まらない製品の終了を失敗にした: $($failures -join '; ')" }
  $checked.Add('正常な終了の終了コードが決まらない製品は、終了コードで失敗にしない')

  # 5. 正常な終了の終了コードが 0 の製品が、例外を示さない 0 以外の値で終了したとき: 失敗にする
  $owned = Start-FakeCad 1
  $failures = @()
  Stop-OwnedCadProcess $owned 30 ([ref]$failures) -QuitExitCode 0
  if (-not @($failures | Where-Object { $_ -match '0x00000001' }).Count) { throw "5: 正常な終了と違う終了コードを失敗にしなかった: $($failures -join '; ')" }
  $checked.Add('正常な終了と違う終了コードで終了した CAD は失敗にする')

  "compat_process_exit_selftest OK（確かめた約束 $($checked.Count) 件）"
  $checked | ForEach-Object { "  - $_" }
}
finally {
  if (Test-Path -LiteralPath $evidenceLog) { Remove-Item -LiteralPath $evidenceLog -Force }
}
