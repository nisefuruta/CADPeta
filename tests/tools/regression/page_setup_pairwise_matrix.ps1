# ページ設定pairwise 12ケースの実機駆動。ケース定義（page_setup_pairwise_cases.ps1）の因子を
# 計測器 CPSETPAIRWISE で実レイアウトへ焼き、PETA の成果物を独立オラクルで判定する。
#   - PDF ページ寸法: MediaBox を独立パーサで読む（製品コードのレンダ経路を参照しない）
#   - SVG ページ寸法: PDF と一致すること
#   - 診断: NoMatchingSize だけが用紙縮退（A4フォールバック）を報告し、「既定用紙で進める」は全ケース禁止
#   - 前後状態: Layout DBObject 非変更・レジストリ復元・入力DWG不変は selection_plot_matrix.ps1 が判定する
[CmdletBinding()]
param(
    [string]$Ver = '2026',
    [string]$OutRoot = "$env:TEMP\CadPeta_page_setup_pairwise",
    [string]$Config = 'Debug',
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot 'selection_plot_matrix.ps1'
. "$PSScriptRoot\page_setup_pairwise_cases.ps1"
# 被覆が崩れたケース表のまま実機を回さない
[void](Assert-CadPetaPageSetupPairwiseCoverage)

# 期待用紙寸法クラス（mm）。因子から独立に決める（製品コードの式を参照しない）。
#   SameName / DifferentNameSameSize: A3（420×297）
#   NoMatchingSize: 一致用紙なし→既定フォールバックの ISO A4（210×297）
# 回転による向きの正規化は製品・ドライバ差があり、その意味論は peta_window_rotation_compat.ps1 が
# 実機の描画一致で担う。ここでは用紙の寸法クラスと、SVG＝PDF表示寸法の厳密一致を判定する。
function Get-ExpectedPaperSizeMm($case) {
    if ($case.MediaScenario -eq 'NoMatchingSize') { return [pscustomobject]@{ Short = 210.0; Long = 297.0 } }
    return [pscustomobject]@{ Short = 297.0; Long = 420.0 }
}

# PDFのMediaBox（物理ページ）と/Rotate（表示回転）を独立パーサで読む。
# 表示寸法 = /Rotate 90/270 なら MediaBox の縦横入替。SVG はこの表示寸法と一致しなければならない。
function Get-PdfPageInfo([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $text = [Text.Encoding]::GetEncoding(28591).GetString($bytes)   # Latin-1でバイト透過に読む
    $found = [regex]::Matches($text, '/MediaBox\s*\[\s*(-?[0-9.]+)\s+(-?[0-9.]+)\s+(-?[0-9.]+)\s+(-?[0-9.]+)\s*\]')
    if ($found.Count -eq 0) { throw "PDFのMediaBoxを読めない: $path" }
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $sizes = foreach ($m in $found) {
        $x0 = [double]::Parse($m.Groups[1].Value, $inv); $y0 = [double]::Parse($m.Groups[2].Value, $inv)
        $x1 = [double]::Parse($m.Groups[3].Value, $inv); $y1 = [double]::Parse($m.Groups[4].Value, $inv)
        [pscustomobject]@{
            W = [Math]::Round(($x1 - $x0) * 25.4 / 72.0, 4)
            H = [Math]::Round(($y1 - $y0) * 25.4 / 72.0, 4)
        }
    }
    $distinct = @($sizes | Sort-Object W, H -Unique)
    if ($distinct.Count -ne 1) {
        throw "PDF内のMediaBoxが一意でない: $path ($(($distinct | ForEach-Object { "$($_.W)x$($_.H)" }) -join ', '))"
    }
    $rotates = @([regex]::Matches($text, '/Rotate\s+(-?\d+)') | ForEach-Object { ([int]$_.Groups[1].Value % 360 + 360) % 360 } | Sort-Object -Unique)
    if ($rotates.Count -gt 1) { throw "PDF内の/Rotateが一意でない: $path ($($rotates -join ', '))" }
    $rotate = if ($rotates.Count -eq 1) { $rotates[0] } else { 0 }
    $display = if ($rotate -in @(90, 270)) {
        [pscustomobject]@{ W = $distinct[0].H; H = $distinct[0].W }
    } else {
        [pscustomobject]@{ W = $distinct[0].W; H = $distinct[0].H }
    }
    [pscustomobject]@{ Page = $distinct[0]; Rotate = $rotate; Display = $display }
}

$summaries = @()
foreach ($case in Get-CadPetaPageSetupPairwiseCases) {
    & $runner -Ver $Ver -OutRoot $OutRoot -Config $Config -TimeoutSeconds $TimeoutSeconds `
        -Command PETA -Space $case.Space -PageSetupCase $case.Id

    $work = Join-Path $OutRoot ("PETA-$($case.Space)-psp-" + $case.Id.ToLowerInvariant())
    $stdout = Join-Path $work 'stdout.log'
    $pdf = @(Get-ChildItem -LiteralPath $work -Filter '*.pdf' -Recurse)
    $svg = @(Get-ChildItem -LiteralPath $work -Filter '*.svg' -Recurse | Where-Object { $_.Name -notmatch '\.stage\d+\.' })
    if ($pdf.Count -ne 1 -or $svg.Count -ne 1) { throw "$($case.Id): 成果物数が不正 pdf=$($pdf.Count) svg=$($svg.Count)" }

    $expected = Get-ExpectedPaperSizeMm $case
    $info = Get-PdfPageInfo $pdf[0].FullName
    $page = $info.Page
    $toleranceMm = 1.5
    $pageShort = [Math]::Min($page.W, $page.H)
    $pageLong = [Math]::Max($page.W, $page.H)
    if ([Math]::Abs($pageShort - $expected.Short) -gt $toleranceMm -or [Math]::Abs($pageLong - $expected.Long) -gt $toleranceMm) {
        throw "$($case.Id): PDF用紙寸法が期待クラスと不一致 期待=$($expected.Short)×$($expected.Long)mm 実=$($page.W)×$($page.H)mm"
    }

    $svgText = Get-Content -LiteralPath $svg[0].FullName -Raw
    if ($svgText -notmatch '<svg[^>]*\bwidth="([0-9.]+)mm"[^>]*\bheight="([0-9.]+)mm"') {
        throw "$($case.Id): SVG寸法を読めない: $($svg[0].FullName)"
    }
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $svgW = [double]::Parse($Matches[1], $inv)
    $svgH = [double]::Parse($Matches[2], $inv)
    if ([Math]::Abs($svgW - $info.Display.W) -gt 0.5 -or [Math]::Abs($svgH - $info.Display.H) -gt 0.5) {
        throw "$($case.Id): SVGがPDF表示寸法（MediaBox×/Rotate）と一致しない SVG=${svgW}×${svgH}mm 表示=$($info.Display.W)×$($info.Display.H)mm (/Rotate=$($info.Rotate))"
    }

    $fallbackReported = [bool](Select-String -Path $stdout -Encoding Unicode -Pattern '用紙縮退' -Quiet)
    if (($case.MediaScenario -eq 'NoMatchingSize') -ne $fallbackReported) {
        throw "$($case.Id): 用紙縮退診断がシナリオと不一致 scenario=$($case.MediaScenario) fallback=$fallbackReported"
    }
    if (Select-String -Path $stdout -Encoding Unicode -Pattern '既定用紙で進める' -Quiet) {
        throw "$($case.Id): 用紙未確定のまま既定用紙で発行された"
    }

    $markerMatch = @(Select-String -Path $stdout -Encoding Unicode -Pattern "CPSETPAIRWISE_OK id=$($case.Id) [^\r\n]*")
    if ($markerMatch.Count -lt 1) { throw "$($case.Id): 計測器の適用証跡が読めない" }
    $summaries += ("{0}: {1}/{2}/{3}/rot{4}/{5} PDF={6}x{7}mm /Rotate={8} SVG={9}x{10}mm [{11}]" -f $case.Id,
        $case.Space, $case.Units, $case.ScaleKind, $case.Rotation, $case.MediaScenario,
        $page.W, $page.H, $info.Rotate, $svgW, $svgH, $markerMatch[0].Matches[0].Value)
}

$summaries | ForEach-Object { $_ }
"ページ設定pairwise実機マトリクス OK: $($summaries.Count)/12 ケース（PDF/SVG寸法・縮退診断・Layout非変更・レジストリ復元）"
