# SHX 不在エラー経路の検証: 実在しない SHX フォント（cadpeta_missing_font.shx）を指す
# スタイルのTEXTを1個だけ持つ図面をLISPで生成し、PETADATA→全選択で
# (a) コマンドが正常完了する (b) 診断ログに「フォントファイルを解決できず近似」が出る
# (c) 出力 SVG では代替書体の text になっていることを確認する。
param(
    [string]$Ver = "2026",
    [string]$OutDir = "$env:TEMP\cadpeta_p3\shx_missing_font",
    [string]$Repo = "C:\Users\wk\repo\CADPeta"
)
$ErrorActionPreference = "Stop"
$repo = $Repo
$tfm = switch ($Ver) { "2024" { "net48" } "2026" { "net8.0-windows" } "2027" { "net10.0-windows" } }
$acad = "C:\Program Files\Autodesk\AutoCAD $Ver\accoreconsole.exe"
$plugin = "$repo\src\CadPeta.Plugin.Acad\bin\Debug\$tfm\CadPeta.Plugin.Acad.dll"
$lsp = "C:\Users\wk\repo\CADPeta\tests\tools\cad-typography\gen_missing_shx.lsp"
if (-not (Test-Path $plugin)) { throw "プラグイン未ビルド: $plugin" }
if (-not (Test-Path $lsp)) { throw "LISP がない: $lsp" }
# 保存済み DWG（TESTCHART2.dwg のコピー）を土台にする。テンプレートからの未保存図面は
# 保存先パス確定の追加プロンプトで応答行数がずれるため使わない。
New-Item -ItemType Directory -Force $OutDir | Out-Null
Remove-Item "$OutDir\*" -Recurse -Force -ErrorAction SilentlyContinue
$lspCopy = Join-Path $OutDir "gen_missing_shx.lsp"
Copy-Item $lsp $lspCopy
$dwg = Join-Path $OutDir "missing_shx.dwg"
Copy-Item "$repo\tests\assets\testcharts\TESTCHART2.dwg" $dwg

$scr = Join-Path $OutDir "run.scr"
@"
(setvar "SECURELOAD" 0)
(load "$($lspCopy -replace '\\','/')")
GENMISSINGSHX
NETLOAD
$plugin
PETADATA
L


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
