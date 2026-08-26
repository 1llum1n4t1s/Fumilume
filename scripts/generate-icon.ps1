[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$iconDirectory = Join-Path $repoRoot 'src\Fumilume\icon'
[System.IO.Directory]::CreateDirectory($iconDirectory) | Out-Null
$pngPath = Join-Path $iconDirectory 'app_icon.png'
$icoPath = Join-Path $iconDirectory 'app.ico'

$bitmap = [System.Drawing.Bitmap]::new(256, 256, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    try {
        $path.AddArc(8, 8, 56, 56, 180, 90)
        $path.AddArc(192, 8, 56, 56, 270, 90)
        $path.AddArc(192, 192, 56, 56, 0, 90)
        $path.AddArc(8, 192, 56, 56, 90, 90)
        $path.CloseFigure()

        $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            [System.Drawing.Rectangle]::new(0, 0, 256, 256),
            [System.Drawing.Color]::FromArgb(255, 64, 136, 255),
            [System.Drawing.Color]::FromArgb(255, 75, 62, 210),
            45.0)
        try {
            $graphics.FillPath($gradient, $path)
        }
        finally {
            $gradient.Dispose()
        }
    }
    finally {
        $path.Dispose()
    }

    $glow = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(35, 255, 255, 255))
    try {
        $graphics.FillEllipse($glow, -34, -56, 250, 190)
    }
    finally {
        $glow.Dispose()
    }

    $font = [System.Drawing.Font]::new('Segoe UI', 146, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $textBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $format = [System.Drawing.StringFormat]::new()
    try {
        $format.Alignment = [System.Drawing.StringAlignment]::Center
        $format.LineAlignment = [System.Drawing.StringAlignment]::Center
        $graphics.DrawString('F', $font, $textBrush, [System.Drawing.RectangleF]::new(0, -4, 256, 256), $format)
    }
    finally {
        $format.Dispose()
        $textBrush.Dispose()
        $font.Dispose()
    }

    $bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

# PNG を 1 枚収録した 256px ICO コンテナーを生成する。
$pngBytes = [System.IO.File]::ReadAllBytes($pngPath)
$stream = [System.IO.File]::Create($icoPath)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]1)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$pngBytes.Length)
    $writer.Write([uint32]22)
    $writer.Write($pngBytes)
}
finally {
    $writer.Dispose()
}

Write-Host "Generated: $pngPath"
Write-Host "Generated: $icoPath"
