# 文字アウトライン化オプション（Phase 3 節4）の Office 貼付・分解確認。
# tests/tools/regression/office_paste_regression.ps1 の流儀（$OutDir は事前に New-Item・Ungroup 再帰で
# 葉まで分解して TextFrame を持つ図形を数える）を Excel 単体・file 経路に絞って踏襲する。
# 通常 EMF（文字保持）と --text-outline EMF（文字を輪郭図形へ展開済み）の2本を同条件で
# 貼付・分解し、寸法・図形数・文字図形数を対比する。検証専用で src/ には関与しない。
#
# 目視素材について: 本機は現セッションが Active でも GDI CopyFromScreen が全黒を返し、
# Excel Shape.CopyPicture / Shape.Copy も 0x800A03EC・CLIPBRD_E_CANT_OPEN で失敗する
# （実測。画面/クリップボードへの実アクセスができないセッション状態と判断。CADPeta 本体
# とは無関係の環境要因）。このため「分解後の見た目」はスクリーンショットではなく、
# 同一 EMF ファイルを System.Drawing.Imaging.Metafile でメモリ上に直接ラスタライズした
# 画像で代替する（Excel の「図形に変換」は EMF の記録済み描画命令をそのまま図形へ展開する
# 処理のため、EMF 自体の直接レンダは分解後の見た目と等価な確認材料になる）。
param(
    [string]$NormalEmf = "$env:TEMP\cadpeta_textoutline\2_office\normal.emf",
    [string]$OutlineEmf = "$env:TEMP\cadpeta_textoutline\2_office\outline.emf",
    [string]$OutDir = "$env:TEMP\cadpeta_textoutline\2_office"
)
$ErrorActionPreference = "Continue"
New-Item -ItemType Directory -Force $OutDir | Out-Null
$jsonPath = Join-Path $OutDir "excel_text_outline_compare.json"
$logPath = Join-Path $OutDir "excel_text_outline_compare.log"
"" | Set-Content $logPath
function Log($msg) { $msg | Add-Content $logPath; Write-Host $msg }
function PtToMm($pt) { return [Math]::Round($pt * 25.4 / 72, 2) }

if (-not (Test-Path $NormalEmf)) { throw "通常 EMF がない: $NormalEmf" }
if (-not (Test-Path $OutlineEmf)) { throw "アウトライン EMF がない: $OutlineEmf" }

Add-Type -AssemblyName System.Drawing

function Recurse-Ungroup($shape) {
    $queue = New-Object System.Collections.ArrayList
    $queue.Add($shape) | Out-Null
    $leaves = New-Object System.Collections.ArrayList
    $firstError = $null
    $anySuccess = $false
    while ($queue.Count -gt 0) {
        $s = $queue[0]; $queue.RemoveAt(0)
        try {
            $range = $s.Ungroup()
            $anySuccess = $true
            for ($i = 1; $i -le $range.Count; $i++) { $queue.Add($range.Item($i)) | Out-Null }
        } catch {
            if ($null -eq $firstError) { $firstError = $_.Exception.Message }
            $leaves.Add($s) | Out-Null
        }
    }
    if (-not $anySuccess) { return @{ success = $false; error = $firstError; leafCount = 0; leaves = @() } }
    return @{ success = $true; error = $null; leafCount = $leaves.Count; leaves = @($leaves) }
}

function Get-ShapeText($s) {
    try { return $s.TextFrame.Characters().Text } catch { return $null }
}

# 画面/クリップボードを介さない直接ラスタライズ（分解後の見た目の代替材料）
function Export-EmfDirectRender($emfPath, $pngPath, $dpi = 150.0) {
    $mf = New-Object System.Drawing.Imaging.Metafile($emfPath)
    try {
        $header = $mf.GetMetafileHeader()
        $wPx = [int]($mf.Width * $dpi / $header.DpiX)
        $hPx = [int]($mf.Height * $dpi / $header.DpiY)
        $bmp = New-Object System.Drawing.Bitmap $wPx, $hPx
        $bmp.SetResolution($dpi, $dpi)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.Clear([System.Drawing.Color]::White)
            $g.DrawImage($mf, 0, 0, $wPx, $hPx)
            $bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
        } finally { $g.Dispose(); $bmp.Dispose() }
    } finally { $mf.Dispose() }
    Log "  直接レンダ: $pngPath"
}

$results = New-Object System.Collections.ArrayList
$excel = $null
try {
    $excel = New-Object -ComObject Excel.Application
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    Log "=== Excel $($excel.Version) ==="
    foreach ($case in @(
        @{ name = "normal"; emf = $NormalEmf; label = "通常EMF（文字保持）" },
        @{ name = "outline"; emf = $OutlineEmf; label = "--text-outlineEMF（輪郭化）" }
    )) {
        $r = [ordered]@{ case = $case.name; label = $case.label }
        $wb = $null
        try {
            Log "--- Excel [$($case.name)] $($case.label) ---"
            $wb = $excel.Workbooks.Add()
            $ws = $wb.Worksheets.Item(1)
            $shape = $ws.Shapes.AddPicture($case.emf, 0, -1, 10, 10, -1, -1)
            $r.widthMm = PtToMm $shape.Width
            $r.heightMm = PtToMm $shape.Height
            Log "  実寸: $($r.widthMm) x $($r.heightMm) mm"

            $ug = Recurse-Ungroup $shape
            $r.ungroupSuccess = $ug.success
            $r.ungroupError = $ug.error
            $r.leafShapeCount = $ug.leafCount
            if ($ug.success) {
                $texts = 0
                foreach ($s in $ug.leaves) {
                    $t = Get-ShapeText $s
                    if ($null -ne $t -and $t.Length -gt 0) { $texts++ }
                }
                $r.textShapeCount = $texts
            }
            Log "  分解: success=$($ug.success) leaves=$($ug.leafCount) textShapes=$($r.textShapeCount) error=$($ug.error)"
        } catch { $r.caseError = $_.Exception.Message; Log "  CASE ERROR: $($_.Exception.Message)" }
        finally { if ($wb) { try { $wb.Close($false) } catch {} } }
        $results.Add($r) | Out-Null

        try { Export-EmfDirectRender $case.emf (Join-Path $OutDir "$($case.name)_direct_render.png") }
        catch { Log "  直接レンダ失敗: $($_.Exception.Message)" }
    }
} catch { Log "Excel FATAL: $($_.Exception.Message)" }
finally { if ($excel) { try { $excel.Quit() } catch {}; [Runtime.InteropServices.Marshal]::ReleaseComObject($excel) | Out-Null } }

($results | ConvertTo-Json -Depth 6) | Set-Content -Encoding UTF8 $jsonPath
Log "JSON: $jsonPath"
Log "ALL DONE"
