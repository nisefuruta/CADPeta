# cli_option_matrix.ps1 -Set Foreign が使う他所産 PDF コーパスの生成
# （Word COM・Excel COM・Edge 印刷の3producer。環境のアプリで生成するためコミットせず、
# 実行前にこのスクリプトで作る）。生成後は COM を確実に Quit し、生成物の実在を確認する。
# 使い方: pwsh -File tests/tools/regression/cli_option_corpus.ps1
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\common\test_helpers.ps1"   # ConvertTo-NativeArgString（Start-Process の引数を引用符で囲む）
$dir = "$env:TEMP\CadPeta_cli_corpus"
New-Item -ItemType Directory -Force $dir | Out-Null

# --- Word: 見出し・本文・表・図形を含む1ページ ---
$wordPdf = Join-Path $dir 'word_sample.pdf'
$word = New-Object -ComObject Word.Application
try {
    $word.Visible = $false
    $doc = $word.Documents.Add()
    $sel = $word.Selection
    $sel.Style = $doc.Styles.Item('見出し 1')
    $sel.TypeText('CADPeta 受領文書サンプル')
    $sel.TypeParagraph()
    $sel.Style = $doc.Styles.Item('標準')
    $sel.TypeText('Word から書き出したベクタ PDF。文字・表・図形を含む。Abc 123 施工図')
    $sel.TypeParagraph()
    $table = $doc.Tables.Add($sel.Range, 3, 3)
    $table.Borders.Enable = $true
    for ($r = 1; $r -le 3; $r++) { for ($c = 1; $c -le 3; $c++) { $table.Cell($r, $c).Range.Text = "R$r C$c" } }
    $shape = $doc.Shapes.AddShape(1, 300, 300, 120, 60)  # 1=四角形
    $shape.Fill.ForeColor.RGB = 0x4080FF
    $doc.SaveAs2($wordPdf, 17)  # 17 = wdFormatPDF
    $doc.Close($false)
}
finally { $word.Quit(); [void][Runtime.InteropServices.Marshal]::ReleaseComObject($word) }

# --- Excel: 罫線付きセル・塗りセルの範囲を1ページへ ---
$excelPdf = Join-Path $dir 'excel_sample.pdf'
$excel = New-Object -ComObject Excel.Application
try {
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $wb = $excel.Workbooks.Add()
    $ws = $wb.Worksheets.Item(1)
    for ($r = 1; $r -le 6; $r++) { for ($c = 1; $c -le 4; $c++) { $ws.Cells.Item($r, $c).Value2 = "S$r-$c" } }
    $rng = $ws.Range('A1:D6')
    $rng.Borders.LineStyle = 1
    $ws.Range('A1:D1').Interior.Color = 0x99CCFF
    $ws.PageSetup.PrintArea = 'A1:D6'
    $wb.ExportAsFixedFormat(0, $excelPdf)  # 0 = xlTypePDF
    $wb.Close($false)
}
finally { $excel.Quit(); [void][Runtime.InteropServices.Marshal]::ReleaseComObject($excel) }

# --- Edge(Chromium) 印刷: ベクタ図形＋文字の HTML ---
$html = Join-Path $dir 'edge_sample.html'
@'
<!doctype html><meta charset="utf-8"><style>body{font-family:sans-serif;margin:20mm}</style>
<h1>Chromium 印刷サンプル</h1>
<p>ベクタ図形と文字を含む受領PDFの再現。Abc 123 図面</p>
<svg width="400" height="200" viewBox="0 0 400 200">
  <rect x="10" y="10" width="150" height="80" fill="#3080e0" stroke="#000"/>
  <circle cx="250" cy="60" r="40" fill="none" stroke="#c03030" stroke-width="3"/>
  <path d="M10 150 L390 150 L200 190 Z" fill="#40a040"/>
  <text x="180" y="120" font-size="20">SVG text 施工図</text>
</svg>
'@ | Out-File $html -Encoding utf8
$edgePdf = Join-Path $dir 'edge_sample.pdf'
$edge = "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edge)) { $edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe" }
$p = Start-Process -FilePath $edge -ArgumentList (ConvertTo-NativeArgString @('--headless=new', '--disable-gpu', "--print-to-pdf=$edgePdf", ([Uri]$html).AbsoluteUri)) -PassThru -WindowStyle Hidden
if (-not $p.WaitForExit(60000)) { Stop-Process -Id $p.Id -Force; $p.WaitForExit() }

foreach ($f in @($wordPdf, $excelPdf, $edgePdf)) {
    if (-not (Test-Path $f) -or (Get-Item $f).Length -lt 1000) { throw "生成に失敗: $f" }
    "生成: $f ($((Get-Item $f).Length) bytes)"
}