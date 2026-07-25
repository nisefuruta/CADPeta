$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common

$sizes = @(16, 24, 32, 48, 64, 256)
$frames = foreach ($size in $sizes) {
    $bmp = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $g = [Drawing.Graphics]::FromImage($bmp)
        try {
            $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $g.Clear([Drawing.Color]::FromArgb(255, 19, 46, 76))
            $line = [Drawing.Pen]::new([Drawing.Color]::FromArgb(150, 145, 190, 220), [Math]::Max(1, $size / 18))
            try {
                $g.DrawLine($line, $size * .10, $size * .70, $size * .68, $size * .12)
                $g.DrawLine($line, $size * .08, $size * .45, $size * .42, $size * .11)
            } finally { $line.Dispose() }
            $shadow = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(90, 0, 0, 0))
            $note = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 255, 210, 61))
            try {
                $g.FillRectangle($shadow, $size * .34, $size * .38, $size * .55, $size * .51)
                $g.FillRectangle($note, $size * .29, $size * .32, $size * .55, $size * .51)
            } finally { $shadow.Dispose(); $note.Dispose() }
        } finally { $g.Dispose() }
        $ms = [IO.MemoryStream]::new(); $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); ,$ms.ToArray(); $ms.Dispose()
    } finally { $bmp.Dispose() }
}

$target = Join-Path $PSScriptRoot '..\app.ico'
$stream = [IO.File]::Create($target); $writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    for ($i=0; $i -lt $frames.Count; $i++) {
        $s=$sizes[$i]; $writer.Write([byte]$(if($s -eq 256){0}else{$s})); $writer.Write([byte]$(if($s -eq 256){0}else{$s}))
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset); $offset += $frames[$i].Length
    }
    foreach($frame in $frames){ $writer.Write($frame) }
} finally { $writer.Dispose(); $stream.Dispose() }
Write-Host "Generated $target"
