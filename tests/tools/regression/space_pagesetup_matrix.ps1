# 同一DWGの Model/Paper に非対称なページ設定を与え、Layout内MSPACEがModel設定を使うことを検査する。
[CmdletBinding()]
param(
    [string]$Ver = '2026',
    [string]$OutRoot = "$env:TEMP\CadPeta_space_pagesetup",
    [string]$Config = 'Debug',
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot 'selection_plot_matrix.ps1'

foreach ($space in 'Model', 'LayoutModel', 'Paper') {
    & $runner -Ver $Ver -OutRoot $OutRoot -Config $Config -TimeoutSeconds $TimeoutSeconds `
        -Command PETA -Space $space -AsymmetricPageSettings
}

function Read-MainSvg([string]$space) {
    $dir = Join-Path $OutRoot "PETA-$space-asymmetric-pages"
    $svg = @(Get-ChildItem -LiteralPath $dir -Filter '*.svg' -File | Where-Object { $_.Name -notmatch '\.stage\d+\.' })
    if ($svg.Count -ne 1) { throw "$space の本体SVG数が不正: $($svg.Count)" }
    $text = Get-Content -LiteralPath $svg[0].FullName -Raw
    if ($text -notmatch '<svg[^>]*\bwidth="([0-9.]+)mm"[^>]*\bheight="([0-9.]+)mm"') {
        throw "$space のSVG寸法を読めない: $($svg[0].FullName)"
    }
    [xml]$xml = $text
    $elementSignature = ($xml.SelectNodes('//*') | Group-Object LocalName | Sort-Object Name |
        ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ','
    [pscustomobject]@{
        Path = $svg[0].FullName
        Width = [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)
        Height = [double]::Parse($Matches[2], [Globalization.CultureInfo]::InvariantCulture)
        Hash = (Get-FileHash -LiteralPath $svg[0].FullName -Algorithm SHA256).Hash
        ElementSignature = $elementSignature
    }
}

$model = Read-MainSvg 'Model'
$layoutModel = Read-MainSvg 'LayoutModel'
$paper = Read-MainSvg 'Paper'

if ([Math]::Abs($model.Width - $layoutModel.Width) -gt 0.0001 -or
    [Math]::Abs($model.Height - $layoutModel.Height) -gt 0.0001) {
    throw "Model と LayoutModel のページ寸法が一致しない: Model=$($model.Width)×$($model.Height) LayoutModel=$($layoutModel.Width)×$($layoutModel.Height)mm"
}
if ($model.ElementSignature -ne $layoutModel.ElementSignature) {
    throw "Model と LayoutModel のSVG要素構成が一致しない: Model=$($model.ElementSignature) LayoutModel=$($layoutModel.ElementSignature)"
}
if ([Math]::Abs($model.Width - 297) -gt 1 -or [Math]::Abs($model.Height - 210) -gt 1) {
    throw "ModelのA4・90度ページ寸法が不正: $($model.Width)×$($model.Height)mm"
}
if ([Math]::Abs($paper.Width - 420) -gt 1 -or [Math]::Abs($paper.Height - 297) -gt 1) {
    throw "PaperのA3・0度ページ寸法が不正: $($paper.Width)×$($paper.Height)mm"
}
if ([Math]::Abs($model.Width - $paper.Width) -lt 1 -and [Math]::Abs($model.Height - $paper.Height) -lt 1) {
    throw '非対称化したModel/Paperの成果物寸法が同じになり、取り違えを識別できない'
}

"空間／ページ設定マトリクス OK: Model=LayoutModel=$($model.Width)×$($model.Height)mm Paper=$($paper.Width)×$($paper.Height)mm"
