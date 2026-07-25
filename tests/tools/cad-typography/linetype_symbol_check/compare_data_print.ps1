# 構造SVG書き出し／PDF印刷結果の突き合わせレンダ: CAD(0,0)を同一ピクセルへ位置合わせしてChromeヘッドレスで
# PNG 化し、tests/tools/cad-typography/render_diff.py で並置・差分・異画素率を出す。
# 位置合わせの根拠:
#   data: 文書写像でCAD(0,0) → svg (0,0)。viewBox原点は(-0.125, -103.1059)
#   print: プロット窓(-1,-6)..(481,192)・尺度1:2・基点0,0。CAD(0,0) → svg (0.5, 294.0389)
#      （実測: E1 の最初のダッシュ始端が (0.4974, 294.0438)、期待値と5µm一致）
$ErrorActionPreference = "Stop"
$out = "C:\Users\wk\AppData\Local\Temp\cadpeta_p3b_ltsym\data_print_compare"
New-Item -ItemType Directory -Force $out | Out-Null
$chrome = "C:\Program Files\Google\Chrome\Application\chrome.exe"
$dpi = 150.0
$s = $dpi / 25.4
# ビューポート: CAD 全域 (-11..485, -6..194) を 1:2 で包含 → 260×115mm、原点アンカー (10, 100)mm
$vwMm = 260.0; $vhMm = 115.0
$vw = [int][Math]::Round($vwMm * $s); $vh = [int][Math]::Round($vhMm * $s)
$anchorX = 10.0; $anchorY = 100.0

function New-Wrapper([string]$svgPath, [double]$originSvgX, [double]$originSvgY, [string]$htmlPath) {
    $svgUri = ([Uri]$svgPath).AbsoluteUri
    $head = Get-Content $svgPath -Encoding utf8 -TotalCount 1
    $m = [regex]::Match($head, 'viewBox="([-\d.]+) ([-\d.]+) ([\d.]+) ([\d.]+)"')
    $vminx = [double]$m.Groups[1].Value; $vminy = [double]$m.Groups[2].Value
    $w = [double]$m.Groups[3].Value; $h = [double]$m.Groups[4].Value
    # CAD(0,0) の svg 内位置（viewBox 左上基準）= (originSvgX - vminx, originSvgY - vminy)
    $leftPx = ($anchorX - ($originSvgX - $vminx)) * $s
    $topPx = ($anchorY - ($originSvgY - $vminy)) * $s
    @"
<!doctype html><html><head><meta charset="utf-8"><style>
html,body{margin:0;padding:0;background:#fff}
img{position:absolute;left:${leftPx}px;top:${topPx}px;width:$($w * $s)px;height:$($h * $s)px}
</style></head><body><img src="$svgUri"></body></html>
"@ | Out-File $htmlPath -Encoding utf8
}

New-Wrapper "C:\Users\wk\AppData\Local\Temp\cadpeta_p3b_ltsym\data-export\test_chart_Model_data.svg" 0 0 "$out\data.html"
New-Wrapper "C:\Users\wk\AppData\Local\Temp\cadpeta_p3b_ltsym\print-output\print.svg" 0.5 294.0389 "$out\print.html"
foreach ($side in "data", "print") {
    & $chrome --headless=new --disable-gpu --hide-scrollbars --default-background-color=FFFFFFFF `
        --window-size="$vw,$vh" --screenshot="$out\$side.png" "$out\$side.html" 2>$null | Out-Null
}
Push-Location (Resolve-Path (Join-Path $PSScriptRoot "..\..\pdf-emf"))
uv run python "..\..\tests\tools\cad-typography\render_diff.py" "$out\print.png" "$out\data.png" "$out\diffdir"
Pop-Location
