# 画面の範囲を PNG に保存する関数。画面の確認（common\ribbon_ui.ps1）と Office の検証（regression\office_doc_verify.ps1）が読み込む。
Add-Type -AssemblyName System.Drawing

# 画面の範囲（x, y, width, height）を取り込んで path に PNG で保存する
function Save-ScreenImage([int]$x, [int]$y, [int]$width, [int]$height, [string]$path) {
    $shot = New-Object Drawing.Bitmap $width, $height
    $g = [Drawing.Graphics]::FromImage($shot); $g.CopyFromScreen($x, $y, 0, 0, $shot.Size); $g.Dispose()
    $shot.Save($path, [Drawing.Imaging.ImageFormat]::Png); $shot.Dispose()
}
