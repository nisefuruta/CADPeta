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
$repo = "C:\Users\wk\repo\CADPeta"
$chrome = "C:\Program Files\Google\Chrome\Application\chrome.exe"
New-Item -ItemType Directory -Force $OutDir | Out-Null

$dpi = 150.0
$s = $dpi / 25.4                 # px/mm
$vwMm = 402.0; $vhMm = 282.0     # 図枠 400x280 + 余白1mm
$vw = [int][Math]::Round($vwMm * $s); $vh = [int][Math]::Round($vhMm * $s)
$anchorX = 1.0; $anchorY = 281.0 # CAD(0,0) を置くビューポート位置（mm）

function New-Wrapper([string]$svgPath, [double]$vminx, [double]$vminy,
                     [double]$ox, [double]$oy, [string]$htmlPath) {
    $svgUri = ([Uri]$svgPath).AbsoluteUri
    # SVG の寸法（mm）を viewBox から読む
    $head = Get-Content $svgPath -Encoding utf8 -TotalCount 1
    $m = [regex]::Match($head, 'viewBox="([-\d.]+) ([-\d.]+) ([\d.]+) ([\d.]+)"')
    $w = [double]$m.Groups[3].Value; $h = [double]$m.Groups[4].Value
    $leftPx = ($anchorX - ($ox - $vminx)) * $s
    $topPx = ($anchorY - ($oy - $vminy)) * $s
    @"
<!doctype html><html><head><meta charset="utf-8"><style>
html,body{margin:0;padding:0;background:#fff}
img{position:absolute;left:${leftPx}px;top:${topPx}px;width:$($w * $s)px;height:$($h * $s)px}
</style></head><body><img src="$svgUri"></body></html>
"@ | Out-File $htmlPath -Encoding utf8
}

New-Wrapper $DataSvg $DataViewMinX $DataViewMinY $DataOriginX $DataOriginY "$OutDir\data.html"
New-Wrapper $PrintSvg $PrintViewMinX $PrintViewMinY $PrintOriginX $PrintOriginY "$OutDir\print.html"
foreach ($side in "data", "print") {
    & $chrome --headless=new --disable-gpu --hide-scrollbars --default-background-color=FFFFFFFF `
        --window-size="$vw,$vh" --screenshot="$OutDir\$side.png" "$OutDir\$side.html" 2>$null | Out-Null
}
Push-Location "$repo\tests\tools\pdf-emf"
uv run python compare_tiles.py "$OutDir\print.png" "$OutDir\data.png" "$OutDir\tiles"
Pop-Location
