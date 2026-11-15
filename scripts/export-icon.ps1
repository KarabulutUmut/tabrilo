<#
.SYNOPSIS
Renders the original folded-ribbon SVG into Windows icon PNG frames and an ICO resource.
.DESCRIPTION
Run this script with pwsh -STA on Windows.
#>
[CmdletBinding()]
param([switch] $Help)
if ($Help) { Get-Help $PSCommandPath; exit 0 }
$ErrorActionPreference = 'Stop'
try {
    Add-Type -AssemblyName WindowsBase,PresentationCore,PresentationFramework
    $root = Split-Path $PSScriptRoot -Parent
    [xml]$svg = Get-Content -LiteralPath (Join-Path $root 'shared/design/tabrilo.svg') -Raw
    $frames = [Collections.Generic.List[object]]::new()
    foreach ($size in 16,24,32,48,64,128,256) {
        $visual = [Windows.Media.DrawingVisual]::new()
        $drawing = $visual.RenderOpen()
        try {
            $scale = $size / 256.0
            $drawing.PushTransform([Windows.Media.ScaleTransform]::new($scale, $scale))
            foreach ($shape in $svg.DocumentElement.ChildNodes | Where-Object NodeType -eq Element) {
                $ink = [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($shape.fill))
                if ($shape.LocalName -eq 'rect') {
                    $bounds = [Windows.Rect]::new([double]$shape.x,[double]$shape.y,[double]$shape.width,[double]$shape.height)
                    $drawing.DrawRoundedRectangle($ink, $null, $bounds, [double]$shape.rx, [double]$shape.rx)
                } elseif ($shape.LocalName -eq 'path') {
                    $geometry = [Windows.Media.Geometry]::Parse($shape.d)
                    $drawing.DrawGeometry($ink, $null, $geometry)
                } else { throw "Unsupported icon shape $($shape.LocalName)." }
            }
            $drawing.Pop()
        } finally { $drawing.Close() }
        $image = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
        $image.Render($visual)
        $png = [Windows.Media.Imaging.PngBitmapEncoder]::new()
        $png.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($image))
        $memory = [IO.MemoryStream]::new()
        try { $png.Save($memory); $frames.Add(@{ Size = $size; Png = $memory.ToArray() }) }
        finally { $memory.Dispose() }
    }

    $target = Join-Path $root 'apps/windows/Tabrilo.Windows/Tabrilo.ico'
    $writer = [IO.BinaryWriter]::new([IO.File]::Create($target))
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $iconSize = [byte]($frame.Size % 256)
            $writer.Write($iconSize)
            $writer.Write($iconSize)
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Png.Length)
            $writer.Write([uint32]$offset)
            $offset += $frame.Png.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Png) }
    } finally { $writer.Dispose() }
    Write-Output $target
    exit 0
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
