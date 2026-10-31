# 互換 CAD の COM の試験の、CAD を起動する前の約束を確かめる（compat_cad_com.ps1）。CAD の代わりに cmd.exe を起動する。
# - 画面の接続が切れたセッションで起動できない製品（ARES 2027）は、環境を変える前に理由を書いて止める（Get-CompatSessionFailure）
# - CAD は、CADPeta の設定のフォルダ（CADPETA_DATA_DIR）を渡して起動し、渡さなければ起動しない（Start-CadProcessHidden）
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$evidenceLog = Join-Path $env:TEMP "compat_launch_selftest_$PID.log"
$PollMilliseconds = 100
. "$PSScriptRoot\compat_cad_com.ps1"

$checked = New-Object System.Collections.Generic.List[string]
# WTS_CONNECTSTATE_CLASS の値（wtsapi32.h）
$wtsActive = 0
$wtsDisconnected = 4

# 1. 接続が要る製品で、画面に接続しているとき: 止めない
$failure = Get-CompatSessionFailure 'Ares2027' $wtsActive
if ($null -ne $failure) { throw "1: 接続しているのに止めた: $failure" }
$checked.Add('接続が要る製品でも、画面に接続していれば止めない')

# 2. 接続が要る製品で、接続が切れているとき: 理由を返す
$failure = Get-CompatSessionFailure 'Ares2027' $wtsDisconnected
if ($failure -notmatch '切断' -or $failure -notmatch 'Ares2027') { throw "2: 接続が切れているのに止めないか、理由に製品と状態が無い: $failure" }
$checked.Add('接続が要る製品は、接続が切れていれば製品と状態を書いた理由を返す')

# 3. 接続が切れていても起動できる製品（ARES 2024）: 止めない
$failure = Get-CompatSessionFailure 'Ares2024' $wtsDisconnected
if ($null -ne $failure) { throw "3: 切断中も起動できる製品を止めた: $failure" }
$checked.Add('切断中も起動できる製品は止めない')

# 4. この PowerShell のセッションの接続の状態を読める（WTSQuerySessionInformation が失敗しない）
$state = Get-CurrentSessionConnectState
if ($state -isnot [int]) { throw "4: 接続の状態を整数で読めなかった: $state" }
$checked.Add("このセッションの接続の状態を読める（今の値 $state）")

# 5. 起動する CAD に渡す環境変数に CADPETA_DATA_DIR が無ければ、起動せずに止める（利用者の %APPDATA%\CADPeta を読ませない）
$cmd = [IO.Path]::GetFullPath((Join-Path $env:SystemRoot 'System32\cmd.exe'))
$work = Join-Path ([IO.Path]::GetTempPath()) ("CadPeta_compat_launch_selftest_" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
  $marker = Join-Path $work 'started.txt'
  $threw = $false
  try { [void](Start-CadProcessHidden $cmd @('/c', "echo started>$marker") @{ OTHER = '1' }) } catch { $threw = $_.Exception.Message -match 'CADPETA_DATA_DIR' }
  Start-Sleep -Milliseconds 500
  if (-not $threw -or (Test-Path -LiteralPath $marker)) { throw "5: CADPETA_DATA_DIR が無いのに起動したか、理由を返さなかった" }
  $checked.Add('CADPETA_DATA_DIR を渡さなければ、CAD を起動せずに止める')

  # 6. 渡した CADPETA_DATA_DIR は起動したプロセスにだけ届き、この PowerShell の環境変数は元のまま
  $dataDir = Join-Path $work 'data'
  $seen = Join-Path $work 'seen.txt'
  $before = [Environment]::GetEnvironmentVariable('CADPETA_DATA_DIR', 'Process')
  $started = Start-CadProcessHidden $cmd @('/c', "echo %CADPETA_DATA_DIR%>$seen") @{ CADPETA_DATA_DIR = $dataDir }
  if (-not $started.WaitForExit(30000)) { throw '6: 起動したプロセスが終わらない' }
  $value = (Get-Content -LiteralPath $seen -Raw).Trim()
  if ($value -ne $dataDir) { throw "6: 起動したプロセスに CADPETA_DATA_DIR が届かない: $value" }
  if ([Environment]::GetEnvironmentVariable('CADPETA_DATA_DIR', 'Process') -ne $before) { throw '6: この PowerShell の CADPETA_DATA_DIR が変わったまま' }
  $checked.Add('CADPETA_DATA_DIR は起動したプロセスにだけ届き、この PowerShell の環境変数は元のまま')
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }

"compat_launch_selftest OK（確かめた約束 $($checked.Count) 件）"
$checked | ForEach-Object { "  - $_" }