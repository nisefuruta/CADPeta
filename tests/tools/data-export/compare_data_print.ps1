# 構造SVG書き出しとPDF印刷経路の突き合わせレンダ: 両 SVG を CAD 原点(0,0)=図枠左下で位置合わせした
# ラッパ HTML を作り、Chrome ヘッドレス 150dpi で PNG 化して compare_tiles.py で機械比較する。
param(
    [string]$DataSvg = "$env:TEMP\cadpeta_p2a\obj_smoke_diag\TESTCHART2_Model_data.svg",
    [string]$PrintSvg = "$env:TEMP\cadpeta_p2a\baseline_cli\tc_off.svg",
    [string]$OutDir = "$env:TEMP\cadpeta_p2a\data_print_compare",
    # 各 SVG の viewBox 原点と CAD(0,0) の viewBox 座標（mm）
    [double]$DataViewMinX = -0.3, [double]$DataViewMinY = -280.3, [double]$DataOriginX = 0, [double]$DataOriginY = 0,
    [double]$PrintViewMinX = 0, [double]$PrintViewMinY = 0, [double]$PrintOriginX = 10.0013, [double]$PrintOriginY = 288.5405
)
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\..\common\svg_compare.ps1"
$repo = "C:\Users\wk\repo\CADPeta"
$chrome = $CadPetaChromeExe
New-Item -ItemType Directory -Force $OutDir | Out-Null

$dpi = 150.0
$s = $dpi / 25.4                 # px/mm
$vwMm = 402.0; $vhMm = 282.0     # 図枠 400x280 + 余白1mm
$vw = ConvertTo-Pixels $vwMm $s; $vh = ConvertTo-Pixels $vhMm $s
$anchorX = 1.0; $anchorY = 281.0 # CAD(0,0) を置くビューポート位置（mm）

New-SvgWrapper $DataSvg $DataViewMinX $DataViewMinY $DataOriginX $DataOriginY "$OutDir\data.html"
New-SvgWrapper $PrintSvg $PrintViewMinX $PrintViewMinY $PrintOriginX $PrintOriginY "$OutDir\print.html"
foreach ($side in "data", "print") {
    & $chrome --headless=new --disable-gpu --hide-scrollbars --default-background-color=FFFFFFFF `
        --window-size="$vw,$vh" --screenshot="$OutDir\$side.png" "$OutDir\$side.html" 2>$null | Out-Null
}
Push-Location "$repo\tests\tools\pdf-emf"
uv run python compare_tiles.py "$OutDir\print.png" "$OutDir\data.png" "$OutDir\tiles"
Pop-Location
