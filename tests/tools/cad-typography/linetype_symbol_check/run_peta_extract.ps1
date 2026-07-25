param(
    [string]$Ver = "2026",
    [string]$Repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")).Path,
    [string]$Dwg = "C:\Users\wk\AppData\Local\Temp\cadpeta_p3b_ltsym\test_chart.dwg",
    [string]$OutDir = "C:\Users\wk\AppData\Local\Temp\cadpeta_p3b_ltsym\extract"
)
$ErrorActionPreference = "Stop"
$tfm = switch ($Ver) { "2024" { "net48" } "2026" { "net8.0-windows" } "2027" { "net10.0-windows" } }
$acad = "C:\Program Files\Autodesk\AutoCAD $Ver\accoreconsole.exe"
$plugin = "$Repo\src\CadPeta.Plugin.Acad\bin\Debug\$tfm\CadPeta.Plugin.Acad.dll"
if (-not (Test-Path $plugin)) { throw "プラグイン未ビルド: $plugin" }
New-Item -ItemType Directory -Force $OutDir | Out-Null
Remove-Item "$OutDir\*" -Recurse -Force -ErrorAction SilentlyContinue
$dwgCopy = Join-Path $OutDir "test_chart.dwg"
Copy-Item $Dwg $dwgCopy

$scr = Join-Path $OutDir "run.scr"
@"
(setvar "SECURELOAD" 0)
NETLOAD
$plugin
PETADATA
ALL


_.QUIT _Y
"@ | Out-File $scr -Encoding ascii

$stdout = Join-Path $OutDir "accore_stdout.log"
# CAD強制終了でも永続sysvar（SECURELOAD等）を復元するため、実行全体をレジストリガードで包む
. "$PSScriptRoot\..\..\regression\CadRegGuard.ps1"
$regGuard = Get-CadRegSnapshot
try {
    & $acad /i $dwgCopy /s $scr /l ja-JP 2>&1 | Out-File $stdout -Encoding utf8
    $cadExitCode = $LASTEXITCODE
}
finally {
    $regReport = Restore-CadRegState $regGuard
    if ($regReport.actions.Count -gt 0) { "レジストリ復元: " + ($regReport.actions -join '; ') }
}
if (-not $regReport.ok) { throw "レジストリ保全の復元検証に失敗: $($regReport.mismatch -join '; ')" }
"exit=$cadExitCode"
Get-ChildItem $OutDir -Filter *.svg -Recurse | ForEach-Object { "{0}  {1:N0} bytes" -f $_.Name, $_.Length }
