# PETADATAの追加検証ケース一括実行:
#  case=tfm2024/tfm2027 : 全選択スモーク（キーワード経路）
#  case=pickfirst       : 先選択（sssetfirst）＋尺度 1:2
#  case=emptyir         : POINT のみの図面（全て未対応→中止のエラー経路）
#  case=zeroscale       : 尺度 0 入力の拒否（再入力で 1）
param(
    [Parameter(Mandatory)][string]$Case,
    [string]$OutDir = "$env:TEMP\cadpeta_p2a\case_$Case"
)
$ErrorActionPreference = "Stop"
$repo = "C:\Users\wk\repo\CADPeta"
$Ver = switch ($Case) { "tfm2024" { "2024" } "tfm2027" { "2027" } default { "2026" } }
$tfm = switch ($Ver) { "2024" { "net48" } "2026" { "net8.0-windows" } "2027" { "net10.0-windows" } }
$acad = "C:\Program Files\Autodesk\AutoCAD $Ver\accoreconsole.exe"
$plugin = "$repo\src\CadPeta.Plugin.Acad\bin\Debug\$tfm\CadPeta.Plugin.Acad.dll"
New-Item -ItemType Directory -Force $OutDir | Out-Null
Remove-Item "$OutDir\*" -Recurse -Force -ErrorAction SilentlyContinue

# CADが強制終了しても永続sysvar（SECURELOAD等）を利用者プロファイルへ残さないレジストリ保全ガード
. "$PSScriptRoot\..\regression\CadRegGuard.ps1"
$regGuard = Get-CadRegSnapshot
try {

if ($Case -eq "emptyir") {
    # POINT 1個だけの図面を作ってからPETADATA→全選択
    $dwg = Join-Path $OutDir "points.dwg"
    $gen = @"
(setvar "SECURELOAD" 0)
_.POINT 10,10
NETLOAD
$plugin
PETADATA
ALL


_.QUIT _Y
"@
    $gen | Out-File "$OutDir\run.scr" -Encoding ascii
    & $acad /i "C:\Program Files\Autodesk\AutoCAD 2026\UserDataCache\ja-jp\Template\acadiso.dwt" /s "$OutDir\run.scr" /l ja-JP 2>&1 | Out-File "$OutDir\accore_stdout.log" -Encoding utf8
    $cadExitCode = $LASTEXITCODE
}
else {
    $dwg = Join-Path $OutDir "TESTCHART2.dwg"
    Copy-Item "$repo\tests\assets\testcharts\TESTCHART2.dwg" $dwg
    $body = switch ($Case) {
        "pickfirst" { @"
(setvar "PICKFIRST" 1)
(sssetfirst nil (ssget "_C" (list 48.0 100.0) (list 220.0 140.0)))
PETADATA
2
"@ }
        "zeroscale" { @"
PETADATA
ALL

0
1
"@ }
        default { @"
PETADATA
ALL


"@ }
    }
    @"
(setvar "SECURELOAD" 0)
NETLOAD
$plugin
$body
_.QUIT _Y
"@ | Out-File "$OutDir\run.scr" -Encoding ascii
    & $acad /i $dwg /s "$OutDir\run.scr" /l ja-JP 2>&1 | Out-File "$OutDir\accore_stdout.log" -Encoding utf8
    $cadExitCode = $LASTEXITCODE
}

}
finally {
    $regReport = Restore-CadRegState $regGuard
    if ($regReport.actions.Count -gt 0) { "レジストリ復元: " + ($regReport.actions -join '; ') }
}
if (-not $regReport.ok) { throw "レジストリ保全の復元検証に失敗: $($regReport.mismatch -join '; ')" }
"exit=$cadExitCode"
Get-ChildItem $OutDir -Filter *.svg -Recurse | ForEach-Object { "{0}  {1:N0} bytes" -f $_.Name, $_.Length }
