[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
  [string]$Accore = "C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe",
  [string]$Dwt = "C:\Program Files\Autodesk\AutoCAD 2026\UserDataCache\ja-jp\Template\acadiso.dwt",
  [string]$Plugin = "",
  [string]$OutRoot = "",
  [int]$TimeoutSec = 180
)
# PETADATAグラデーションハッチE2Eスモーク（accoreconsoleヘッドレス）。
# 9形状のグラデーションハッチをentmakeで作り、先選択から構造SVGを生成して検査する。
#
# 起動方式の必須事項（2026-07-14 実測）:
# - accoreconsole は「& exe ... | Out-File」や外部シェルの標準出力リダイレクトでは
#   終了・フラッシュせずハングする。Start-Process -RedirectStandardOutput で起動し、
#   WaitForExit(timeout) で待ち、残留したら Kill する（gen_run.ps1 と同一規律）。
# - .scr の先頭で FILEDIA 0 を立てる。既定 1 のままだと SAVEAS が GUI ファイル
#   ダイアログを待ってハングする（ヘッドレスにダイアログは出ない）。
# - Kill は FixedProfile\General Configuration\FileDialog=0 をレジストリへ永続化
#   するため、終了時に必ず点検・復元する。
$ErrorActionPreference = "Stop"
# AutoCAD・互換 CAD の設定を変える前に確認する（-WhatIf で計画だけを表示する。自動の実行は -Confirm:$false）
. "$PSScriptRoot\..\common\test_helpers.ps1"
$changePlan = New-AcadE2ePlan 'gradient_e2e_autocad.ps1' $OutRoot
Write-ChangePlan $changePlan
Exit-UnlessConfirmed $PSCmdlet.ShouldProcess($changePlan.Target, $changePlan.Action)
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
. "$PSScriptRoot\..\common\test_helpers.ps1"
if (-not $Plugin) { $Plugin = Get-E2ePluginPath Acad 'net8.0-windows' }
if (-not $OutRoot) { $OutRoot = Join-Path $env:TEMP "CADPeta_gradient_e2e_autocad" }
New-Item -ItemType Directory -Force $OutRoot | Out-Null
$dwg = (Join-Path $OutRoot "gradient_e2e.dwg") -replace '\\','/'
$lsp = (Resolve-Path (Join-Path $PSScriptRoot "gradient_e2e.lsp")).Path -replace '\\','/'
$pluginPath = (Resolve-Path $Plugin).Path -replace '\\','/'
$scr = Join-Path $OutRoot "gradient_e2e.scr"
$stdout = Join-Path $OutRoot "gradient_e2e.stdout.txt"
Get-ChildItem $OutRoot -Filter "gradient_e2e*.svg" -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $OutRoot -Filter "gradient_e2e*.emf" -ErrorAction SilentlyContinue | Remove-Item -Force
if (Test-Path $dwg) { Remove-Item -LiteralPath $dwg -Force }

$lines = @(
  '(setvar "FILEDIA" 0)',
  '(setvar "CMDDIA" 0)'
) + (Get-AccoreLoadLispLines $lsp) + @(
  "GRADIENTE2E",
  "_.SAVEAS", "2018", $dwg
) + (Get-AccoreNetloadLines $pluginPath) + @(
  "(sssetfirst nil (ssget `"_X`" '((0 . `"HATCH`"))))",
  "PETADATA", "",
  "_.QUIT", "_Y"
)
[IO.File]::WriteAllLines($scr, $lines, [Text.Encoding]::GetEncoding(932))

# CAD のタイムアウト・強制終了でも永続 sysvar（FILEDIA/SECURELOAD/OSMODE 等）を利用者プロファイルへ残さない包括ガード
. "$PSScriptRoot\..\regression\CadRegGuard.ps1"
$regGuard = Get-CadRegSnapshot -StageToTrusted @($lsp, $Plugin) -AcadVersion (Get-AcadVersionFromPath $Accore)
$runError = $null
try {
  $proc = Start-Process -WorkingDirectory (Split-Path -Parent $scr) -FilePath $Accore `
            -ArgumentList (ConvertTo-NativeArgString @('/i', $Dwt, '/s', $scr, '/l', 'ja-JP')) `
            -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout
  if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
    try { $proc.Kill() } catch {}
    Write-Warning "accoreconsole が ${TimeoutSec} 秒で完了しなかったため終了しました"
  }

  # Kill 有無に関わらず FileDialog=0 の永続化を点検・復元する
  # （FileDialog=1 への矯正のみで、外側ガードの安全値矯正と同方向のため二重復元でも矛盾しない）
  $keys = Get-ChildItem "HKCU:\Software\Autodesk\AutoCAD" -Recurse -ErrorAction SilentlyContinue |
          Where-Object { $_.Name -match "General Configuration$" }
  foreach ($k in $keys) {
    $v = Get-ItemProperty -LiteralPath $k.PSPath -Name FileDialog -ErrorAction SilentlyContinue
    if ($v -and $v.FileDialog -eq 0) {
      Set-ItemProperty -LiteralPath $k.PSPath -Name FileDialog -Value 1
      "レジストリ復元: $($k.Name) FileDialog 0->1"
    }
  }
}
catch { $runError = $_ }
finally {
  $regReport = $null # [ref] で受け取る変数を先に作る
  Restore-CadRegStateAndReport $regGuard ([ref]$regReport)
}
Assert-CadRunAndRegRestore $runError $regReport

$svg = Get-ChildItem $OutRoot -Filter "gradient_e2e*.svg" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $svg) { throw "PETADATA の出力が見つかりません（$stdout）" }
"DWG=$dwg"
"SVG=$($svg.FullName)"
$svgText = [IO.File]::ReadAllText($svg.FullName)
$imageCount = ([regex]::Matches($svgText, "data:image/png")).Count
"SVG data:image/png count=$imageCount (期待=9)"
$decodedStdout = [IO.File]::ReadAllText($stdout, [Text.Encoding]::Unicode)
$decodedStdout -split "`r?`n" | Select-String -Pattern "GRADIENTE2E|グラデーション:|エラー|Error|例外|図形"
