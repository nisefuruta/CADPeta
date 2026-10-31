#Requires -Version 7
<#
.SYNOPSIS
リボンのボタンのアイコン（ICO）を、元の絵の SVG から作る。

.DESCRIPTION
<名前>-32.svg を 24・32・48 px、<名前>-16.svg を 16 px で Edge（ヘッドレス）に描かせて PNG にし、1 つの <名前>.ico にまとめる。
16 px は縮小せずに描き直した絵を使う。Edge は一時のプロファイル（-PngFolder の下）で起動し、利用者の Edge のプロファイルに触れない。
作った ICO は CadPeta.Plugin.Acad.csproj と CadPeta.Plugin.Ares.csproj が DLL に埋め込む。

.PARAMETER PngFolder
途中の PNG と Edge の一時のプロファイルを置くフォルダ（確認のために PNG を残す）。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PngFolder,
    [string]$Edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# アイコンの名前と、絵ごとの大きさ（16 px は 16 用の絵、それ以外は 32 用の絵）
$Icons = 'Peta', 'PetaOpt'
$Sizes = @{ 16 = '16'; 24 = '32'; 32 = '32'; 48 = '32' }
# Edge の画面の大きさ（最も大きいアイコンより大きければよい）
$ShotSize = 256

if (-not (Test-Path -LiteralPath $Edge)) { throw "Edge が見つからない: $Edge" }
New-Item -ItemType Directory -Force -Path $PngFolder | Out-Null
$profileDir = Join-Path $PngFolder 'edge-profile'

function Render-Png([string]$svg, [int]$size, [string]$png) {
    $html = Join-Path $PngFolder ([IO.Path]::GetFileNameWithoutExtension($png) + '.html')
    $svgUri = ([Uri](Resolve-Path -LiteralPath $svg).Path).AbsoluteUri
    Set-Content -LiteralPath $html -Encoding utf8 -Value "<html><body style='margin:0;background:transparent'><img src='$svgUri' width='$size' height='$size'></body></html>"
    $shot = Join-Path $PngFolder ('shot-' + [IO.Path]::GetFileName($png))
    & $Edge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 --default-background-color=00000000 `
        "--user-data-dir=$profileDir" "--window-size=$ShotSize,$ShotSize" "--screenshot=$shot" ([Uri]$html).AbsoluteUri 2>$null | Out-Null
    if (-not (Test-Path -LiteralPath $shot)) { throw "Edge が画像を書かなかった: $svg ($size px)" }
    $full = [Drawing.Bitmap]::FromFile($shot)
    try {
        $crop = $full.Clone([Drawing.Rectangle]::new(0, 0, $size, $size), [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try { $crop.Save($png, [Drawing.Imaging.ImageFormat]::Png) } finally { $crop.Dispose() }
    } finally { $full.Dispose() }
}

# PNG の画像をそのまま入れた ICO（Windows Vista 以降の形。WPF の IconBitmapDecoder が読める）
function Write-Ico([string]$ico, [string[]]$pngs) {
    $images = $pngs | ForEach-Object { , [IO.File]::ReadAllBytes($_) }
    $stream = [IO.MemoryStream]::new()
    $w = [IO.BinaryWriter]::new($stream)
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    for ($i = 0; $i -lt $images.Count; $i++) {
        $size = [Drawing.Image]::FromFile($pngs[$i]); $px = $size.Width; $size.Dispose()
        $w.Write([byte]($px % 256)); $w.Write([byte]($px % 256)); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $w.Write($image) }
    $w.Flush()
    [IO.File]::WriteAllBytes($ico, $stream.ToArray())
}

foreach ($icon in $Icons) {
    $pngs = foreach ($size in ($Sizes.Keys | Sort-Object)) {
        $png = Join-Path $PngFolder "$icon-$size.png"
        Render-Png (Join-Path $PSScriptRoot "$icon-$($Sizes[$size]).svg") $size $png
        $png
    }
    $ico = Join-Path $PSScriptRoot "$icon.ico"
    Write-Ico $ico $pngs
    Write-Host "作った: $ico（$(($Sizes.Keys | Sort-Object) -join '・') px）"
}