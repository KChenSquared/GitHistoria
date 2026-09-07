#Requires -Version 5.1
<#
.SYNOPSIS
    Composite the two Historia launch-splash PNGs (light + dark), emblem and wordmark
    baked in, ready to drop straight into the SplashWindow.

.DESCRIPTION
    Output (2x of the 400x500 SplashWindow, so it stays crisp on HiDPI):
      assets/splash-light.png  - book emblem + "Historia" in gunmetal #495B6E on white
      assets/splash-dark.png   - book emblem + "Historia" in a silver gradient on black

    Emblem sources are the transparent masters from dev/extract-logos.ps1
    (assets/{light,dark}/historia-*-master.png). The wordmark is Segoe UI Bold, auto-sized
    to a fraction of the canvas width. Its fill matches the book cover in each theme:
    the gunmetal blue of the light-theme book, and a 3-stop silver blend (bright top ->
    steel foot) echoing the metallic sheen of the dark-theme book.

.EXAMPLE
    powershell -File dev/build-splash.ps1
#>
[CmdletBinding()]
param(
    [string] $RepoRoot,
    [string] $OutDir,
    [int]    $Width            = 800,
    [int]    $Height           = 1000,
    [int]    $EmblemH          = 560,
    [int]    $GapPx            = 6,
    [int]    $TopBiasPx        = -12,   # optical centring: nudge the whole stack up
    [double] $TrackingEm       = 0.05,  # letter-spacing as a fraction of the font size
    [double] $MaxTextWidthFrac = 0.72,
    [int]    $MaxFontPx        = 104
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $scriptDir }
if (-not $OutDir)   { $OutDir   = Join-Path $RepoRoot 'assets' }

$word = 'Historia'

# (name, background, emblem master, wordmark fill spec)
$jobs = @(
    [pscustomobject]@{
        Out    = 'splash-light.png'
        Bg     = [System.Drawing.Color]::White
        Emblem = Join-Path $RepoRoot 'assets\light\historia-light-master.png'
        Fill   = @{ Kind = 'solid'; Color = [System.Drawing.Color]::FromArgb(0x49, 0x5B, 0x6E) }
    }
    [pscustomobject]@{
        Out    = 'splash-dark.png'
        Bg     = [System.Drawing.Color]::Black
        Emblem = Join-Path $RepoRoot 'assets\dark\historia-dark-master.png'
        Fill   = @{
            Kind      = 'gradient'
            Colors    = @(
                [System.Drawing.Color]::FromArgb(0xEE, 0xF2, 0xF6),   # bright highlight
                [System.Drawing.Color]::FromArgb(0xCE, 0xD5, 0xDF),   # book silver
                [System.Drawing.Color]::FromArgb(0x98, 0xA4, 0xB2)    # steel foot
            )
            Positions = @(0.0, 0.5, 1.0)
        }
    }
)

# Per-glyph draw so we can add letter-spacing (GDI+ has no tracking on DrawString). The
# brush lives in canvas space, so a single gradient brush spans the whole word correctly.
function Get-GlyphWidths($g, $text, $font, $fmt) {
    $text.ToCharArray() | ForEach-Object { $g.MeasureString([string]$_, $font, 10000, $fmt).Width }
}
function Draw-Tracked($g, $text, $font, $brush, $centerX, $y, $trackPx, $baseFmt) {
    $near = New-Object System.Drawing.StringFormat $baseFmt
    $near.Alignment = [System.Drawing.StringAlignment]::Near
    $widths = @(Get-GlyphWidths $g $text $font $near)
    $total  = ($widths | Measure-Object -Sum).Sum + $trackPx * ($text.Length - 1)
    $x = $centerX - $total / 2.0
    for ($i = 0; $i -lt $text.Length; $i++) {
        $g.DrawString([string]$text[$i], $font, $brush, [single]$x, [single]$y, $near)
        $x += $widths[$i] + $trackPx
    }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

foreach ($j in $jobs) {
    if (-not (Test-Path -LiteralPath $j.Emblem)) { throw "Emblem not found: $($j.Emblem) - run dev/extract-logos.ps1 first." }

    $canvas = New-Object System.Drawing.Bitmap $Width, $Height
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    $g.Clear($j.Bg)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.SmoothingMode     = 'AntiAlias'
    $g.PixelOffsetMode   = 'HighQuality'
    $g.CompositingQuality = 'HighQuality'
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias

    $emblem = [System.Drawing.Bitmap]::new($j.Emblem)
    $emblemW = [int]([Math]::Round($emblem.Width / [double]$emblem.Height * $EmblemH))

    # A private copy of GenericTypographic (tight metrics), centred. Never mutate the shared
    # static instance, and drop LineLimit so a hair of rounding can't blank the whole line.
    $fmt = New-Object System.Drawing.StringFormat ([System.Drawing.StringFormat]::GenericTypographic)
    $fmt.Alignment   = [System.Drawing.StringAlignment]::Center
    $fmt.FormatFlags = [System.Drawing.StringFormatFlags]::NoWrap

    $fontPx = $MaxFontPx
    while ($fontPx -gt 20) {
        $font = New-Object System.Drawing.Font 'Segoe UI', $fontPx, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $sz = $g.MeasureString($word, $font, $Width, $fmt)
        $trackPx = $fontPx * $TrackingEm
        $trackedW = $sz.Width + $trackPx * ($word.Length - 1)
        if ($trackedW -le $Width * $MaxTextWidthFrac) { break }
        $font.Dispose()
        $fontPx -= 2
    }

    $stackH = $EmblemH + $GapPx + [int][Math]::Ceiling($sz.Height)
    $top    = [int][Math]::Round(($Height - $stackH) / 2) + $TopBiasPx

    $emblemRect = New-Object System.Drawing.Rectangle ([int][Math]::Round(($Width - $emblemW) / 2)), $top, $emblemW, $EmblemH
    $g.DrawImage($emblem, $emblemRect)

    $textY = $top + $EmblemH + $GapPx

    if ($j.Fill.Kind -eq 'solid') {
        $brush = New-Object System.Drawing.SolidBrush $j.Fill.Color
    }
    else {
        $gRect = New-Object System.Drawing.RectangleF 0, ([single]($textY - 2)), ([single]$Width), ([single]([Math]::Ceiling($sz.Height) + 4))
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $gRect, $j.Fill.Colors[0], $j.Fill.Colors[-1], ([System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
        $blend = New-Object System.Drawing.Drawing2D.ColorBlend $j.Fill.Colors.Count
        $blend.Colors    = [System.Drawing.Color[]]$j.Fill.Colors
        $blend.Positions = [single[]]$j.Fill.Positions
        $brush.InterpolationColors = $blend
    }

    Draw-Tracked $g $word $font $brush ($Width / 2.0) $textY ($fontPx * $TrackingEm) $fmt

    $brush.Dispose(); $font.Dispose(); $emblem.Dispose(); $g.Dispose()

    $path = Join-Path $OutDir $j.Out
    $canvas.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $canvas.Dispose()
    Write-Host ("  {0}  {1}x{2}  font {3}px" -f $j.Out, $Width, $Height, $fontPx)
}

Write-Host ""
Write-Host "Wrote $OutDir\splash-light.png and $OutDir\splash-dark.png" -ForegroundColor Green
