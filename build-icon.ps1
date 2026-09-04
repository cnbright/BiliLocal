$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetFolder = Join-Path $PSScriptRoot 'BiliLocal.Desktop\Assets'
$source = [Drawing.Bitmap]::FromFile((Join-Path $assetFolder 'BiliLocal.png'))
$frames = [Collections.Generic.List[byte[]]]::new()
$sizes = @(16,24,32,48,64,128,256)
try {
    foreach ($size in $sizes) {
        $frame = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($frame)
        $stream = [IO.MemoryStream]::new()
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($source, [Drawing.Rectangle]::new(0,0,$size,$size))
            $frame.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($stream.ToArray())
        } finally { $stream.Dispose(); $graphics.Dispose(); $frame.Dispose() }
    }
    $file = [IO.File]::Create((Join-Path $assetFolder 'BiliLocal.ico'))
    $writer = [IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frameBytes in $frames) { $writer.Write($frameBytes) }
    } finally { $writer.Dispose() }
    Write-Host "Icon packaged: $($sizes -join ', ') px; source $($source.Width)x$($source.Height), corner alpha $($source.GetPixel(0,0).A)"
} finally { $source.Dispose() }
