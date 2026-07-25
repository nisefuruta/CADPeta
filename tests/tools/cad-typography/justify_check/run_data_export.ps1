# PETADATA: justify_chart.dwgをaccoreconsoleで開き、全選択→尺度Enter（1:1）で構造SVGを取得する。
# 使い方: pwsh -File run_data_export.ps1 [-OutDir <出力先>]
param(
    [string]$OutDir = "$env:TEMP\cadpeta_p3b_justify\data-export"
)
$ErrorActionPreference = "Stop"
$repo = "C:\Users\wk\repo\CADPeta"
$acad = "C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe"
$plugin = "$repo\src\CadPeta.Plugin.Acad\bin\Debug\net8.0-windows\CadPeta.Plugin.Acad.dll"
if (-not (Test-Path $plugin)) { throw "プラグイン未ビルド: $plugin" }
$stray = Get-Process AcCoreConsole -ErrorAction SilentlyContinue
if ($stray) { throw "accoreconsole の残留プロセスがある（PID: $($stray.Id -join ',')）。先に確認すること" }

New-Item -ItemType Directory -Force $OutDir | Out-Null
Remove-Item "$OutDir\*" -Recurse -Force -ErrorAction SilentlyContinue
$dwg = Join-Path $OutDir "justify_chart.dwg"
Copy-Item "$repo\tests\tools\cad-typography\justify_check\justify_chart.dwg" $dwg

# PETADATA → ALL（＋空行で選択確定）→ 尺度Enter（空行、1:1）
$scr = Join-Path $OutDir "run.scr"
@"
(setvar "SECURELOAD" 0)
NETLOAD
$plugin
(setvar "SECURELOAD" 1)
PETADATA
ALL


_.QUIT _Y
"@ | Out-File $scr -Encoding ascii

$stdout = Join-Path $OutDir "accore_stdout.log"
# CAD強制終了でも永続sysvar（SECURELOAD等）を復元するため、実行全体をレジストリガードで包む
. "$PSScriptRoot\..\..\regression\CadRegGuard.ps1"
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
