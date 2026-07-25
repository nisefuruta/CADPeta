# EMF を Excel へ貼り、「図形に変換」で分解した前後の**見た目**を画素で比較する。
#
# なぜ画素比較か: 既存の office_paste_regression.ps1 は寸法・分解数・文字数を測るが、分解後の
# 図形位置を測っていない。かといって「leaf の外接矩形の合成」を親の枠と比べるだけでは足りない。
# それは bbox 集計であり、枠の内側で図形が飛んでも検出できない（実測: 修正前後どちらの EMF でも
# 四辺のずれが 0.000mm と出た）。過去に bbox 集計だけで「直った」と二度誤報している。
# 分解で崩れる不具合は「貼付・表示は正常、分解すると崩れる」という
# 現れ方をするため、分解前後のレンダを画素で突き合わせるのが確実な判定になる。
#
# レンダの取り方: チャート経由の画像化（CopyPicture→Chart.Export）は Excel の汎用エラー
# 0x800A03EC で不安定だったため、シートの PDF 書き出しを使う。分解の前後で同じページ設定の
# まま書き出すので、差はシート上の図形だけに由来する。
#
# 使い方:
#   pwsh -File tests/tools/regression/emf_ungroup_geometry.ps1 -Emf <emf>
#
# 判定: 分解前後のレンダが一致すること。差があれば非0終了し、差分画像を残す。
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Emf,
    # 利用者の実操作はクリップボード貼付。ファイル挿入とは別経路のため切り替えて測れるようにする
    # （Windows は CF_ENHMETAFILE を置くと CF_METAFILEPICT を自動合成する。どちらを Office が
    #  受け取るかで結果が変わり得る）
    [switch]$Clipboard,
    [string]$OutDir = "$env:TEMP\CadPeta_ungroup_geometry",
    # 一致とみなす差分画素の割合（%）。分解は図形を個別化するため、アンチエイリアスの
    # かかり方が僅かに変わり得る。図形が動く不具合はこの桁では説明できない量になる。
    [double]$MaxDiffPercent = 0.5,
    [int]$Dpi = 200
)
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
if (-not (Test-Path $Emf)) { throw "EMF がない: $Emf" }
$Emf = (Resolve-Path $Emf).Path
$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
New-Item -ItemType Directory -Force $OutDir | Out-Null
Get-ChildItem $OutDir -Filter "*.pdf" -ErrorAction SilentlyContinue | Remove-Item -Force

function PtToMm([double]$pt) { return $pt * 25.4 / 72.0 }

# 分解は再帰。Office は入れ子グループを返すため、葉に至るまで Ungroup を繰り返す
function Expand-Leaves($shape) {
    $queue = New-Object System.Collections.ArrayList
    [void]$queue.Add($shape)
    $leaves = New-Object System.Collections.ArrayList
    $firstError = $null
    $anySuccess = $false
    while ($queue.Count -gt 0) {
        $s = $queue[0]; $queue.RemoveAt(0)
        try {
            $range = $s.Ungroup()
            $anySuccess = $true
            for ($i = 1; $i -le $range.Count; $i++) { [void]$queue.Add($range.Item($i)) }
        } catch {
            if ($null -eq $firstError) { $firstError = $_.Exception.Message }
            [void]$leaves.Add($s)
        }
    }
    return @{ AnySuccess = $anySuccess; FirstError = $firstError; Leaves = @($leaves) }
}

function Export-SheetPdf($book, [string]$path) {
    $book.ExportAsFixedFormat(0, $path) | Out-Null  # 0 = xlTypePDF
    if (-not (Test-Path $path)) { throw "PDF が書き出せなかった: $path" }
}

# PETA と同一の API 列でクリップボードへ登録する（office_paste_regression.ps1 と同じ方式）。
# Office の貼り付けはクリップボードを消し得るため、貼付直前に登録する。
Add-Type -Name Clip -Namespace CadPetaUngroup -MemberDefinition @'
[DllImport("user32.dll", SetLastError=true)] public static extern bool OpenClipboard(IntPtr h);
[DllImport("user32.dll")] public static extern bool EmptyClipboard();
[DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SetClipboardData(uint f, IntPtr h);
[DllImport("user32.dll")] public static extern bool CloseClipboard();
[DllImport("gdi32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr GetEnhMetaFileW(string f);
[DllImport("gdi32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr CopyEnhMetaFileW(IntPtr h, string f);
[DllImport("gdi32.dll")] public static extern bool DeleteEnhMetaFile(IntPtr h);
'@

function Set-ClipboardEmf([string]$path) {
    $CF_ENHMETAFILE = 14
    $h = [CadPetaUngroup.Clip]::GetEnhMetaFileW($path)
    if ($h -eq [IntPtr]::Zero) { throw "EMF を読めない: $path" }
    # CopyEnhMetaFileW の第2引数 null はメモリ上の複製を意味する（[NullString]::Value で渡す）
    $hCopy = [CadPetaUngroup.Clip]::CopyEnhMetaFileW($h, [NullString]::Value)
    $null = [CadPetaUngroup.Clip]::DeleteEnhMetaFile($h)
    if ($hCopy -eq [IntPtr]::Zero) { throw "EMF を複製できない" }
    if (-not [CadPetaUngroup.Clip]::OpenClipboard([IntPtr]::Zero)) { throw "OpenClipboard 失敗" }
    try {
        $null = [CadPetaUngroup.Clip]::EmptyClipboard()
        if ([CadPetaUngroup.Clip]::SetClipboardData($CF_ENHMETAFILE, $hCopy) -eq [IntPtr]::Zero) {
            $null = [CadPetaUngroup.Clip]::DeleteEnhMetaFile($hCopy)
            throw "SetClipboardData 失敗"
        }
    } finally { $null = [CadPetaUngroup.Clip]::CloseClipboard() }
}

$officeApp = $null
$book = $null
try {
    $officeApp = New-Object -ComObject Excel.Application
    $officeApp.Visible = $false
    $officeApp.DisplayAlerts = $false
    $book = $officeApp.Workbooks.Add()
    $ws = $book.Worksheets.Item(1)

    if ($Clipboard) {
        # 利用者の実操作と同じ経路。貼付直前に登録する（Office の操作がクリップボードを消し得る）
        Set-ClipboardEmf $Emf
        $before = $ws.Shapes.Count
        $ws.Paste() | Out-Null
        if ($ws.Shapes.Count -ne $before + 1) { throw "クリップボード貼付で図形が増えていない" }
        $shape = $ws.Shapes.Item($ws.Shapes.Count)
        $shape.Left = 0; $shape.Top = 0
    } else {
        $shape = $ws.Shapes.AddPicture($Emf, $false, $true, 0, 0, -1, -1)
    }
    "=== $(Split-Path $Emf -Leaf)（経路: $(if ($Clipboard) { 'クリップボード貼付' } else { 'ファイル挿入' })） ==="
    # 分解すると親図形は消えるため、寸法は分解前に控える（後で読むと 0 が記録される）
    $widthMm = PtToMm $shape.Width
    $heightMm = PtToMm $shape.Height
    "分解前の親図形: {0:F3} x {1:F3} mm" -f $widthMm, $heightMm

    # 分解の前後で同一のページ設定にする（差が図形以外から出ないようにする）
    $ws.PageSetup.Zoom = 100
    $ws.PageSetup.LeftMargin = 0; $ws.PageSetup.RightMargin = 0
    $ws.PageSetup.TopMargin = 0; $ws.PageSetup.BottomMargin = 0
    $ws.PageSetup.HeaderMargin = 0; $ws.PageSetup.FooterMargin = 0

    $beforePdf = Join-Path $OutDir "before.pdf"
    Export-SheetPdf $book $beforePdf
    "分解前のレンダ: $beforePdf"

    $ug = Expand-Leaves $shape
    if (-not $ug.AnySuccess) {
        # 図形数が上限（実測 10,000）を超えると分解自体が COM エラーで失敗する既知制約。
        # 「ずれ」の調査と混同しないよう、ここで切り分けて報告する
        throw "分解できなかった（既知制約の図形数上限に触れた可能性）: $($ug.FirstError)"
    }
    "分解後の leaf 数: $($ug.Leaves.Count)"

    $afterPdf = Join-Path $OutDir "after.pdf"
    Export-SheetPdf $book $afterPdf
    "分解後のレンダ: $afterPdf"

    $diffPng = Join-Path $OutDir "diff.png"
    $wc = "$repo\tests\tools\regression\window_contract"
    $cmp = & uv run --project $wc python "$wc\compare_pages.py" $beforePdf $afterPdf --dpi $Dpi --diff-out $diffPng 2>&1
    $cmp | ForEach-Object { "  $_" }
    $m = $cmp | Select-String -Pattern '総計: .*差分画素=(\d+) \(([\d.]+)%\)' | Select-Object -Last 1
    if (-not $m) { throw ("差分の割合を読み取れなかった:`n" + ($cmp -join "`n")) }
    $diffPercent = [double]$m.Matches.Groups[2].Value

    $result = [ordered]@{
        emf = $Emf; pasteRoute = $(if ($Clipboard) { 'clipboard' } else { 'file' })
        leafCount = $ug.Leaves.Count
        widthMm = $widthMm; heightMm = $heightMm
        diffPercent = $diffPercent; maxDiffPercent = $MaxDiffPercent; dpi = $Dpi
        pass = ($diffPercent -le $MaxDiffPercent)
    }
    $json = Join-Path $OutDir "ungroup_geometry.json"
    $result | ConvertTo-Json -Depth 6 | Set-Content $json -Encoding utf8
    "記録: $json"
    if (-not $result.pass) {
        throw ("分解で見た目が変わった（差分 {0:F4}% > 許容 {1}%）。差分画像: {2}" -f $diffPercent, $MaxDiffPercent, $diffPng)
    }
    "判定: 合格（分解前後のレンダが一致）"
} finally {
    # ブックを保存せずに閉じ、確認モーダルを残さない
    if ($book) { try { $book.Close($false) } catch { } }
    if ($officeApp) { try { $officeApp.Quit() } catch { } }
    foreach ($o in @($book, $officeApp)) {
        if ($o) { try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($o) } catch { } }
    }
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
}
