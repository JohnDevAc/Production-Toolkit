$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$outputRoot = Join-Path $PSScriptRoot '..\src\ToolkitLauncher\Assets'
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = [Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc(($x + $w - $d), $y, $d, $d, 270, 90)
    $p.AddArc(($x + $w - $d), ($y + $h - $d), $d, $d, 0, 90)
    $p.AddArc($x, ($y + $h - $d), $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}
$frames = @()
foreach ($size in @(16,20,24,32,40,48,64,96,128,256)) {
    $bitmap = [Drawing.Bitmap]::new(($size * 4), ($size * 4))
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.ScaleTransform(($size * 4 / 256), ($size * 4 / 256))
    foreach ($rect in @(@(8,8,240,240,52,'#172f3e'), @(48,48,64,64,12,'#b6e6da'), @(144,48,64,64,12,'#73c8b3'), @(48,144,64,64,12,'#73c8b3'))) {
        $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($rect[5]))
        $shape = New-RoundedPath $rect[0] $rect[1] $rect[2] $rect[3] $rect[4]
        $g.FillPath($brush, $shape)
        $brush.Dispose()
        $shape.Dispose()
    }
    $pen = [Drawing.Pen]::new([Drawing.Color]::White,16)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $g.DrawLine($pen,148,204,204,148)
    $g.DrawLines($pen,[Drawing.PointF[]]@([Drawing.PointF]::new(158,148),[Drawing.PointF]::new(204,148),[Drawing.PointF]::new(204,194)))
    $small = [Drawing.Bitmap]::new($size,$size)
    $sg = [Drawing.Graphics]::FromImage($small)
    $sg.InterpolationMode = 'HighQualityBicubic'
    $sg.DrawImage($bitmap,0,0,$size,$size)
    $memory = [IO.MemoryStream]::new()
    $small.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
    $frames += ,@($size,$memory.ToArray())
    if ($size -eq 256) { $small.Save((Join-Path $outputRoot 'toolkit.png'),[Drawing.Imaging.ImageFormat]::Png) }
    $memory.Dispose(); $sg.Dispose(); $small.Dispose(); $pen.Dispose(); $g.Dispose(); $bitmap.Dispose()
}
$file = [IO.File]::Create((Join-Path $outputRoot 'toolkit.ico'))
$writer = [IO.BinaryWriter]::new($file)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames) {
    $dimension = if ($frame[0] -eq 256) { 0 } else { $frame[0] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frame[1].Length); $writer.Write([uint32]$offset)
    $offset += $frame[1].Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame[1]) }
$writer.Dispose()
Write-Host 'Created toolkit.ico (16–256 px) and toolkit.png.'
