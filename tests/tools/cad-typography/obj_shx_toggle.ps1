# PETADATA SHX代替(S)トグル検証: TESTCHART2.dwgをaccoreconsoleで開き、全選択→
# 尺度プロンプトでSを1回入力（SHX代替=オン）→Enterで確定し構造SVGを出力する。
# 既定（Sなし）はtests/tools/data-export/obj_smoke.ps1を使う。SHX代替オンの出力がpre_switch
# ワークツリー（自前 SHX 解釈器の追加前）の既定出力とバイト一致することの確認に使う。
param(
    [string]$Ver = "2026",
    [ValidateSet("PETADATA")][string]$Cmd = "PETADATA",
    [string]$OutDir = "$env:TEMP\cadpeta_p3\obj_shx_toggle",
    [string]$Repo = "C:\Users\wk\repo\CADPeta",
    [string]$SelectResponse = "ALL"
)
$ErrorActionPreference = "Stop"
$repo = $Repo
$tfm = switch ($Ver) { "2024" { "net48" } "2026" { "net8.0-windows" } "2027" { "net10.0-windows" } }
$acad = "C:\Program Files\Autodesk\AutoCAD $Ver\accoreconsole.exe"
$plugin = "$repo\src\CadPeta.Plugin.Acad\bin\Debug\$tfm\CadPeta.Plugin.Acad.dll"
if (-not (Test-Path $plugin)) { throw "プラグイン未ビルド: $plugin" }
New-Item -ItemType Directory -Force $OutDir | Out-Null
Remove-Item "$OutDir\*" -Recurse -Force -ErrorAction SilentlyContinue
$dwg = Join-Path $OutDir "TESTCHART2.dwg"
Copy-Item "$repo\tests\assets\testcharts\TESTCHART2.dwg" $dwg

# PETADATA → 選択（応答＋空行で確定）→ S（SHX代替オン）→ 空行（既定尺度で確定）
$scr = Join-Path $OutDir "run.scr"
@"
(setvar "SECURELOAD" 0)
NETLOAD
$plugin
$Cmd
$SelectResponse

S

_.QUIT _Y
"@ | Out-File $scr -Encoding ascii

$stdout = Join-Path $OutDir "accore_stdout.log"
# CAD強制終了でも永続sysvar（SECURELOAD等）を復元するため、実行全体をレジストリガードで包む
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
