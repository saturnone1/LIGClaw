[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$assetDirectory = Join-Path $repositoryRoot 'src\LIGClaw.Desktop\Assets'
New-Item -ItemType Directory -Force -Path $assetDirectory | Out-Null

function New-RoundedRectanglePath {
    param([float] $X, [float] $Y, [float] $Width, [float] $Height, [float] $Radius)

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function Convert-Points {
    param([int] $Size, [float[][]] $Coordinates)

    $scale = $Size / 64.0
    return [System.Drawing.PointF[]] @($Coordinates | ForEach-Object {
        [System.Drawing.PointF]::new($_[0] * $scale, $_[1] * $scale)
    })
}

function New-LIGClawBitmap {
    param([int] $Size)

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $inset = [Math]::Max(1.0, $Size * 0.035)
    $background = New-RoundedRectanglePath $inset $inset ($Size - 2 * $inset) ($Size - 2 * $inset) ($Size * 0.19)
    $blueBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 0, 47, 109))
    $graphics.FillPath($blueBrush, $background)

    $whiteBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $grayBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 208, 211, 212))
    $clawBody = Convert-Points $Size @(
        @(11, 12), @(39, 12), @(30, 21), @(21, 21),
        @(21, 43), @(30, 43), @(39, 52), @(11, 52)
    )
    $graphics.FillPolygon($whiteBrush, $clawBody)

    foreach ($coordinates in @(
        @(@(38, 12), @(55, 12), @(46, 21), @(29, 21)),
        @(@(38, 28), @(55, 28), @(46, 36), @(29, 36)),
        @(@(38, 43), @(55, 43), @(46, 52), @(29, 52))
    )) {
        $graphics.FillPolygon($grayBrush, (Convert-Points $Size $coordinates))
    }

    $grayBrush.Dispose()
    $whiteBrush.Dispose()
    $blueBrush.Dispose()
    $background.Dispose()
    $graphics.Dispose()
    return $bitmap
}

function Write-MultiSizeIcon {
    param([string] $Path, [int[]] $Sizes)

    $images = foreach ($size in $Sizes) {
        $bitmap = New-LIGClawBitmap $size
        $stream = [System.IO.MemoryStream]::new()
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        ,$stream.ToArray()
        $stream.Dispose()
    }

    $file = [System.IO.File]::Create($Path)
    $writer = [System.IO.BinaryWriter]::new($file)
    $writer.Write([uint16] 0)
    $writer.Write([uint16] 1)
    $writer.Write([uint16] $Sizes.Count)
    $offset = 6 + (16 * $Sizes.Count)

    for ($index = 0; $index -lt $Sizes.Count; $index++) {
        $size = $Sizes[$index]
        $writer.Write([byte] $(if ($size -ge 256) { 0 } else { $size }))
        $writer.Write([byte] $(if ($size -ge 256) { 0 } else { $size }))
        $writer.Write([byte] 0)
        $writer.Write([byte] 0)
        $writer.Write([uint16] 1)
        $writer.Write([uint16] 32)
        $writer.Write([uint32] $images[$index].Length)
        $writer.Write([uint32] $offset)
        $offset += $images[$index].Length
    }

    foreach ($image in $images) {
        $writer.Write($image)
    }

    $writer.Dispose()
    $file.Dispose()
}

$preview = New-LIGClawBitmap 256
$preview.Save((Join-Path $assetDirectory 'ligclaw-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()

Write-MultiSizeIcon (Join-Path $assetDirectory 'ligclaw.ico') @(16, 20, 24, 32, 40, 48, 64, 128, 256)
Write-MultiSizeIcon (Join-Path $assetDirectory 'ligclaw-tray.ico') @(16, 20, 24, 32, 40, 48, 64)

Write-Host "Generated LIGClaw application and tray assets in $assetDirectory"
