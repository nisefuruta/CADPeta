# SVG を CAD 座標のアンカーに合わせて HTML へ貼り、印刷出力と重ねて比べるハーネスが共有する関数。
# 呼び出し側スクリプトがドットソースで読み込む。
# New-SvgWrapper は呼び出し側のスクリプト変数 $anchorX／$anchorY（アンカー位置）と $s（縮尺）を参照する。

# mm を、1 mm あたりのピクセル数 $pxPerMm でピクセル数（整数、四捨五入）へ換算する。
function ConvertTo-Pixels([double]$mm, [double]$pxPerMm) { return [int][Math]::Round($mm * $pxPerMm) }

# SVG 先頭行の viewBox を読み、MinX／MinY／Width／Height を返す。
function Get-SvgViewBox([string]$svgPath) {
    $head = Get-Content $svgPath -Encoding utf8 -TotalCount 1
    $m = [regex]::Match($head, 'viewBox="([-\d.]+) ([-\d.]+) ([\d.]+) ([\d.]+)"')
    return @{
        MinX = [double]$m.Groups[1].Value; MinY = [double]$m.Groups[2].Value
        Width = [double]$m.Groups[3].Value; Height = [double]$m.Groups[4].Value
    }
}

# SVG を 1 枚貼った HTML を書く。$vminx／$vminy は viewBox の左上、$ox／$oy は CAD(0,0) の SVG 内位置。
function New-SvgWrapper([string]$svgPath, [double]$vminx, [double]$vminy,
                        [double]$ox, [double]$oy, [string]$htmlPath) {
    $svgUri = ([Uri]$svgPath).AbsoluteUri
    # SVG の寸法（mm）を viewBox から読む
    $viewBox = Get-SvgViewBox $svgPath
    $w = $viewBox.Width; $h = $viewBox.Height
    $leftPx = ($anchorX - ($ox - $vminx)) * $s
    $topPx = ($anchorY - ($oy - $vminy)) * $s
    @"
<!doctype html><html><head><meta charset="utf-8"><style>
html,body{margin:0;padding:0;background:#fff}
img{position:absolute;left:${leftPx}px;top:${topPx}px;width:$($w * $s)px;height:$($h * $s)px}
</style></head><body><img src="$svgUri"></body></html>
"@ | Out-File $htmlPath -Encoding utf8
}
