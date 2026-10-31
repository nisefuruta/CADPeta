# SVG を CAD 座標のアンカーに合わせて HTML へ貼り、印刷出力と重ねて比べるハーネスが共有する関数。
# 呼び出し側スクリプトがドットソースで読み込む。
# New-SvgWrapper は呼び出し側のスクリプト変数 $anchorX／$anchorY（アンカー位置）と $s（縮尺）を参照する。

# SVG を描く Chrome の場所（PowerShell の比較の試験の 1 か所。Python 側は common/chrome_path.py）
$CadPetaChromeExe = "C:\Program Files\Google\Chrome\Application\chrome.exe"

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

# 描画パス（clipPath の定義を除く）の座標全部の外接矩形と、viewBox の四辺との差（mm）を返す。
# 差が 0 なら内容が辺まで届いている。正なら辺との間に余白があり、負なら内容が辺の外へはみ出している。
# 窓の縁に描いた矩形が四辺まで届くかで、クロップ矩形が実際の印字内容と一致するかを確かめる。
function Get-SvgContentEdgeGaps([string]$svgPath) {
    $viewBox = Get-SvgViewBox $svgPath
    $body = [regex]::Replace((Get-Content $svgPath -Encoding utf8 -Raw), '<defs>.*?</defs>', '', 'Singleline')
    $bounds = Get-SvgPathBounds @([regex]::Matches($body, 'd="([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
    if ($null -eq $bounds) { throw "描画パスの座標が無い: $svgPath" }
    return @{
        ViewBox = $viewBox
        Left = $bounds.MinX - $viewBox.MinX; Top = $bounds.MinY - $viewBox.MinY
        Right = ($viewBox.MinX + $viewBox.Width) - $bounds.MaxX; Bottom = ($viewBox.MinY + $viewBox.Height) - $bounds.MaxY
    }
}

# パスの d 属性（CADPeta の SVG は M/L/C/Z の絶対座標だけを書く。SvgWriter）の座標の対全部の外接矩形。座標が無ければ $null
function Get-SvgPathBounds([string[]]$pathData) {
    $minX = [double]::MaxValue; $minY = [double]::MaxValue
    $maxX = [double]::MinValue; $maxY = [double]::MinValue
    foreach ($d in $pathData) {
        foreach ($pt in [regex]::Matches($d, '(-?[0-9.]+) (-?[0-9.]+)')) {
            $px = [double]$pt.Groups[1].Value; $py = [double]$pt.Groups[2].Value
            if ($px -lt $minX) { $minX = $px }; if ($px -gt $maxX) { $maxX = $px }
            if ($py -lt $minY) { $minY = $py }; if ($py -gt $maxY) { $maxY = $py }
        }
    }
    if ($minX -eq [double]::MaxValue) { return $null }
    return @{ MinX = $minX; MinY = $minY; MaxX = $maxX; MaxY = $maxY }
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
