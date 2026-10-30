# Office COM（Excel／PowerPoint）の EMF 貼り付け・図形分解を確かめるハーネスが共有する関数。
# 呼び出し側スクリプトがドットソースで読み込む。
# Flatten-Shape は呼び出し側のスクリプト変数 $MSO_GROUP（msoGroup = 6）を参照する。

# pt を mm へ換算する。小数の桁数は -Digits で指定する（既定 3 桁）。
function PtToMm($pt, [int]$Digits = 3) { return [Math]::Round($pt * 25.4 / 72, $Digits) }

# EMF をクリップボードへ CF_ENHMETAFILE として登録し、読み戻してファイルのバイト列と一致するか照合する。
# 本番（EmfClipboard.SetFromFile）と同一の API 列で登録し、読み戻し照合まで行う。
# $Clip は呼び出し側が Add-Type で定義した P/Invoke 型（OpenClipboard などを持つもの）。
function Register-Emf([type]$Clip, [string]$Emf, [byte[]]$FileBytes, [uint32]$ClipboardFormat) {
    $hemf = $Clip::GetEnhMetaFileW($Emf)
    if ($hemf -eq [IntPtr]::Zero) { throw "GetEnhMetaFile 失敗: $Emf" }
    # PowerShell は $null を空文字列にマーシャルするため、NULL ポインタは [NullString]::Value で渡す
    $hCopy = $Clip::CopyEnhMetaFileW($hemf, [NullString]::Value)
    $null = $Clip::DeleteEnhMetaFile($hemf)
    if ($hCopy -eq [IntPtr]::Zero) { throw "CopyEnhMetaFile 失敗" }
    if (-not $Clip::OpenClipboard([IntPtr]::Zero)) {
        $null = $Clip::DeleteEnhMetaFile($hCopy); throw "OpenClipboard 失敗"
    }
    try {
        $null = $Clip::EmptyClipboard()
        if ($Clip::SetClipboardData($ClipboardFormat, $hCopy) -eq [IntPtr]::Zero) {
            $null = $Clip::DeleteEnhMetaFile($hCopy); throw "SetClipboardData 失敗"
        }
    } finally { $null = $Clip::CloseClipboard() }
    # 読み戻し照合（本番と同一の登録が成立していることを確認してから貼付する）
    if (-not $Clip::OpenClipboard([IntPtr]::Zero)) { throw "OpenClipboard(照合) 失敗" }
    try {
        $h = $Clip::GetClipboardData($ClipboardFormat)
        $size = $Clip::GetEnhMetaFileBits($h, 0, $null)
        $got = New-Object byte[] $size
        $null = $Clip::GetEnhMetaFileBits($h, $size, $got)
        if (-not [Linq.Enumerable]::SequenceEqual($got, $FileBytes)) { throw "登録内容がファイルと不一致" }
    } finally { $null = $Clip::CloseClipboard() }
}

# 群の図形を最上位から葉まで Ungroup し、葉の数と分解の段数を返す。
# msoGroup（$MSO_GROUP）のときだけ Ungroup を呼ぶので、葉で COM 例外を起こさない。
# 返り値は leafCount（葉の数）と levels。-CollectLeaves を付けると leaves（葉の図形）も返す
# （数万個規模の検証では COM 参照を保持しないよう、既定では集めない）。
function Flatten-Shape($shape, [switch]$CollectLeaves) {
    $queue = New-Object System.Collections.Generic.Queue[object]
    $queue.Enqueue(@{ s = $shape; isTop = $true })
    $leafCount = 0
    $leaves = if ($CollectLeaves) { New-Object System.Collections.Generic.List[object] } else { $null }
    $levels = 0
    while ($queue.Count -gt 0) {
        $item = $queue.Dequeue()
        $s = $item.s
        if ($item.isTop -or $s.Type -eq $MSO_GROUP) {
            $range = $s.Ungroup()
            $levels++
            for ($i = 1; $i -le $range.Count; $i++) { $queue.Enqueue(@{ s = $range.Item($i); isTop = $false }) }
        } else {
            $leafCount++
            if ($CollectLeaves) { $leaves.Add($s) }
        }
    }
    return @{ leafCount = $leafCount; leaves = $leaves; levels = $levels }
}

# Ungroup を呼び、COM 例外になった図形を葉とみなして再帰的に展開する（図形数が少ない検証向け）。
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

# クリップボードの EMF を Excel のワークシートへ貼り付け（日本語版・英語版の形式名の順に試す）、貼り付けた図形を返す。
function Add-EmfShapeByPasteSpecial($ws) {
    try { $ws.PasteSpecial("図 (拡張メタファイル)") }
    catch { $ws.PasteSpecial("Picture (Enhanced Metafile)") }
    return $ws.Shapes.Item($ws.Shapes.Count)
}

# 図形の文字列を返す。取得できなければ $null。$app が Excel のときは Characters()、それ以外は TextRange を読む。
function Get-ShapeText($s, $app = "Excel") {
    try {
        if ($app -eq "Excel") { return $s.TextFrame.Characters().Text }
        else { return $s.TextFrame.TextRange.Text }
    } catch { return $null }
}
