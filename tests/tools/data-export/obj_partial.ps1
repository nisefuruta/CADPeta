# PETADATA部分選択＋レイヤ別グループ化: 交差窓で一部を選択し、Lでレイヤ別グループ化を
# オンにして出力する。
param(
    [string]$Ver = "2026",
    [string]$OutDir = "$env:TEMP\cadpeta_p2a\obj_partial"
)
$ErrorActionPreference = "Stop"
$repo = "C:\Users\wk\repo\CADPeta"
$tfm = switch ($Ver) { "2024" { "net48" } "2026" { "net8.0-windows" } "2027" { "net10.0-windows" } }
$acad = "C:\Program Files\Autodesk\AutoCAD $Ver\accoreconsole.exe"
$plugin = "$repo\src\CadPeta.Plugin.Acad\bin\Debug\$tfm\CadPeta.Plugin.Acad.dll"
New-Item -ItemType Directory -Force $OutDir | Out-Null
Remove-Item "$OutDir\*" -Recurse -Force -ErrorAction SilentlyContinue
$dwg = Join-Path $OutDir "TESTCHART2.dwg"
Copy-Item "$repo\tests\assets\testcharts\TESTCHART2.dwg" $dwg

# PETADATA → 交差窓 (40,25)-(170,115) → 選択確定 → L（レイヤ別グループ化オン）→ 尺度Enter
$scr = Join-Path $OutDir "run.scr"
@"
(setvar "SECURELOAD" 0)
NETLOAD
$plugin
PETADATA
C
40,25
170,115

L

_.QUIT _Y
"@ | Out-File $scr -Encoding ascii

$stdout = Join-Path $OutDir "accore_stdout.log"
# CADが強制終了しても永続sysvar（SECURELOAD等）を利用者プロファイルへ残さないレジストリ保全ガード
. "$PSScriptRoot\..\regression\CadRegGuard.ps1"
$regGuard = Get-CadRegSnapshot
try {
    & $acad /i $dwg /s $scr /l ja-JP 2>&1 | Out-File $stdout -Encoding utf8
    $cadExitCode = $LASTEXITCODE
}
finally {
    $regReport = Restore-CadRegState $regGuard
    if ($regReport.actions.Count -gt 0) { "レジストリ復元: " + ($regReport.actions -join '; ') }
}
if (-not $regReport.ok) { throw "レジストリ保全の復元検証に失敗: $($regReport.mismatch -join '; ')" }
"exit=$cadExitCode"
Get-ChildItem $OutDir -Filter *.svg -Recurse | ForEach-Object { "{0}  {1:N0} bytes" -f $_.Name, $_.Length }
