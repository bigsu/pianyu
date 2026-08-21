param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\Pianyu.App\Assets\pianyu.png')
)

Add-Type -AssemblyName System.Drawing
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$expectedRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\Pianyu.App\Assets'))
if (-not $resolvedOutput.StartsWith($expectedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "图标输出路径必须位于 Assets 目录。"
}

$bitmap = [System.Drawing.Bitmap]::new(256, 256)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)

function New-RoundedPath([System.Drawing.RectangleF]$rect, [float]$radius) {
    $diameter = $radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($rect.X, $rect.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($rect.Right - $diameter, $rect.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($rect.Right - $diameter, $rect.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

$darkBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#101416'))
$accentBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#35D0B0'))
$outer = New-RoundedPath ([System.Drawing.RectangleF]::new(0, 0, 256, 256)) 52
$inner = New-RoundedPath ([System.Drawing.RectangleF]::new(32, 32, 192, 192)) 38
$lineOne = New-RoundedPath ([System.Drawing.RectangleF]::new(64, 78, 128, 36)) 12
$lineTwo = New-RoundedPath ([System.Drawing.RectangleF]::new(64, 136, 96, 36)) 12
$graphics.FillPath($darkBrush, $outer)
$graphics.FillPath($accentBrush, $inner)
$graphics.FillPath($darkBrush, $lineOne)
$graphics.FillPath($darkBrush, $lineTwo)
$graphics.FillPolygon($darkBrush, [System.Drawing.PointF[]]@(
    [System.Drawing.PointF]::new(160, 168),
    [System.Drawing.PointF]::new(184, 168),
    [System.Drawing.PointF]::new(160, 196)
))

$bitmap.Save($resolvedOutput, [System.Drawing.Imaging.ImageFormat]::Png)
$outer.Dispose()
$inner.Dispose()
$lineOne.Dispose()
$lineTwo.Dispose()
$darkBrush.Dispose()
$accentBrush.Dispose()
$graphics.Dispose()
$bitmap.Dispose()
