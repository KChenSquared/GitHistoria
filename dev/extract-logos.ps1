#Requires -Version 5.1
<#
.SYNOPSIS
    Extract the two Historia ship emblems from their JPEG backgrounds (white / black
    keyed to transparency) and render a full set of logo + icon PNGs at every size the
    app and common desktop / web / mobile platforms use.

.DESCRIPTION
    Sources (under assets/, 880x1213):
      * Historia_book_light.jpg - slate-blue book on near-white   -> "light" set
      * Historia_book_dark.jpg  - silver book on near-black       -> "dark"  set

    For each source the script:
      1. Keys the white / black field to transparency EVERYWHERE by colour, with a soft
         (anti-aliased) edge - so the tile interior inside the emblem's border drops out
         too. Colour fringe left by the original JPEG compositing is un-blended so edges
         stay clean on any backdrop. (-FloodFill instead removes only the edge-connected
         background and keeps white / black that the artwork encloses.)
      2. Trims to the emblem bounding box            -> <base>-master.png   (native res)
      3. Centres it on a padded transparent square   -> <base>-square.png   (icon base)
      4. Down-samples the square (HQ bicubic) to every size in -Sizes.
      5. Writes a multi-resolution <base>.ico and a macOS <base>.iconset/ folder.
      6. Writes _preview-512.png (emblem over a checkerboard) so transparency is visible,
         plus historia-mark.png / historia-mark@2x.png named for the app's HomeView asset.

    Output: dev/logo-out/{light,dark}/ . Nothing under Historia.App/Assets is modified -
    copy in the files you want.

    Tuning: inspect _preview-512.png; if edges keep a halo or the emblem is eaten,
    re-run with adjusted -LightRamp* / -DarkRamp* / -*FloodKey values.

.EXAMPLE
    pwsh dev/extract-logos.ps1
.EXAMPLE
    powershell -File dev/extract-logos.ps1 -Sizes 32,64,128,256 -DarkFloodKey 50
#>
[CmdletBinding()]
param(
    [string]  $RepoRoot,
    [string]  $OutDir,
    [string]  $LightSource,
    [string]  $DarkSource,

    # Every icon/logo size the app + Windows + macOS + Linux + web/PWA + mobile touch
    # icons commonly need. Squares, transparent PNG.
    [int[]]   $Sizes        = @(16,20,24,32,40,44,48,60,64,72,96,120,128,144,150,152,167,180,192,256,310,512,1024),
    [int[]]   $IcoSizes     = @(16,24,32,48,64,128,256),

    [double]  $Padding      = 0.06,   # transparent margin around the emblem on the square, as a fraction of its longer side

    # White-background key (min(R,G,B)). Paper ~240-253, emblem body min < 90.
    [int]     $LightFloodKey = 224,   # a pixel this bright (min channel) can be traversed as background
    [int]     $LightRampLo   = 168,   # min channel <= this -> fully opaque
    [int]     $LightRampHi   = 232,   # min channel >= this -> fully transparent

    # Black-background key (max(R,G,B)). Bg ~0-4, emblem body max > 90; faint outer glow ~10-60.
    [int]     $DarkFloodKey  = 42,
    [int]     $DarkRampLo    = 22,    # max channel <= this -> fully transparent (>=12 to swallow JPEG speckle)
    [int]     $DarkRampHi    = 72,    # max channel >= this -> fully opaque

    # By default the white / black field is keyed EVERYWHERE by colour, so the tile
    # interior inside the emblem's border also drops out (spine bands / feather / border
    # stroke are left "floating", which is fine). -FloodFill restores the old behaviour:
    # only the background reachable from the image edge is removed, enclosed white/black kept.
    [switch]  $FloodFill,

    [switch]  $DarkOnly             # emit only the dark set (skip the light job)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $scriptDir }
if (-not $OutDir)   { $OutDir   = Join-Path $scriptDir 'logo-out' }

# The clean full-bleed book emblems on a flat white / black field.
if (-not $LightSource) { $LightSource = Join-Path $RepoRoot 'assets\Historia_book_light.jpg' }
if (-not $DarkSource)  { $DarkSource  = Join-Path $RepoRoot 'assets\Historia_book_dark.jpg' }
foreach ($s in @($LightSource, $DarkSource)) {
    if (-not (Test-Path -LiteralPath $s)) { throw "Source not found: $s" }
}

# --- pixel-level extraction (compiled; PowerShell is too slow for a 4 Mpx loop) ----------
Add-Type -ReferencedAssemblies 'System.Drawing' -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class HistoriaLogo
{
    public static Bitmap Extract(string path, bool dark, int floodKey, int rampLo, int rampHi, bool flood)
    {
        Bitmap src0 = new Bitmap(path);
        int w = src0.Width, h = src0.Height, n = w * h;
        Bitmap src = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(src)) { g.DrawImage(src0, new Rectangle(0, 0, w, h)); }
        src0.Dispose();

        Rectangle rect = new Rectangle(0, 0, w, h);
        BitmapData sd = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] buf = new byte[n * 4];
        Marshal.Copy(sd.Scan0, buf, 0, buf.Length);
        src.UnlockBits(sd);
        src.Dispose();

        bool[] bg = new bool[n];
        if (flood) { Flood(buf, bg, w, h, dark, floodKey); Dilate(bg, w, h, 2); }
        else       { for (int i = 0; i < n; i++) bg[i] = true; }

        double lo = rampLo, hi = rampHi, span = (hi - lo); if (span < 1) span = 1;
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            int b = buf[o], gg = buf[o + 1], r = buf[o + 2];
            double a;
            if (dark) { int mx = r; if (gg > mx) mx = gg; if (b > mx) mx = b; a = (mx - lo) / span; }
            else      { int mn = r; if (gg < mn) mn = gg; if (b < mn) mn = b; a = 1.0 - (mn - lo) / span; }
            if (a < 0) a = 0; else if (a > 1) a = 1;

            if (!bg[i]) { buf[o + 3] = 255; continue; }           // enclosed / not border-connected: keep as-is
            if (a <= 0.0) { buf[o] = 0; buf[o + 1] = 0; buf[o + 2] = 0; buf[o + 3] = 0; continue; }
            if (a < 0.996)                                        // un-blend the key colour out of the fringe
            {
                double inv = 1.0 - a, key = dark ? 0.0 : 255.0;
                buf[o + 2] = Clamp((r  - inv * key) / a);
                buf[o + 1] = Clamp((gg - inv * key) / a);
                buf[o]     = Clamp((b  - inv * key) / a);
            }
            buf[o + 3] = (byte)Math.Round(a * 255.0);
        }

        Bitmap outb = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        BitmapData od = outb.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(buf, 0, od.Scan0, buf.Length);
        outb.UnlockBits(od);
        return CropToContent(outb, 8);
    }

    static byte Clamp(double v) { if (v < 0) return 0; if (v > 255) return 255; return (byte)Math.Round(v); }

    static void Flood(byte[] buf, bool[] bg, int w, int h, bool dark, int key)
    {
        Stack<int> st = new Stack<int>();
        for (int x = 0; x < w; x++) { Seed(buf, bg, st, x, dark, key); Seed(buf, bg, st, (h - 1) * w + x, dark, key); }
        for (int y = 0; y < h; y++) { Seed(buf, bg, st, y * w, dark, key); Seed(buf, bg, st, y * w + w - 1, dark, key); }
        while (st.Count > 0)
        {
            int i = st.Pop(), x = i % w, y = i / w;
            if (x > 0)     Seed(buf, bg, st, i - 1, dark, key);
            if (x < w - 1) Seed(buf, bg, st, i + 1, dark, key);
            if (y > 0)     Seed(buf, bg, st, i - w, dark, key);
            if (y < h - 1) Seed(buf, bg, st, i + w, dark, key);
        }
    }

    static void Seed(byte[] buf, bool[] bg, Stack<int> st, int i, bool dark, int key)
    {
        if (bg[i]) return;
        int o = i * 4, b = buf[o], g = buf[o + 1], r = buf[o + 2];
        bool cand;
        if (dark) { int mx = r; if (g > mx) mx = g; if (b > mx) mx = b; cand = mx <= key; }
        else      { int mn = r; if (g < mn) mn = g; if (b < mn) mn = b; cand = mn >= key; }
        if (cand) { bg[i] = true; st.Push(i); }
    }

    static void Dilate(bool[] m, int w, int h, int r)
    {
        for (int pass = 0; pass < r; pass++)
        {
            bool[] c = (bool[])m.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (!c[i]) continue;
                    if (x > 0)     m[i - 1] = true;
                    if (x < w - 1) m[i + 1] = true;
                    if (y > 0)     m[i - w] = true;
                    if (y < h - 1) m[i + w] = true;
                }
        }
    }

    static Bitmap CropToContent(Bitmap b, int aThresh)
    {
        int w = b.Width, h = b.Height;
        Rectangle rect = new Rectangle(0, 0, w, h);
        BitmapData d = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] buf = new byte[w * h * 4];
        Marshal.Copy(d.Scan0, buf, 0, buf.Length);
        b.UnlockBits(d);

        int minx = w, miny = h, maxx = -1, maxy = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (buf[(y * w + x) * 4 + 3] > aThresh)
                {
                    if (x < minx) minx = x; if (x > maxx) maxx = x;
                    if (y < miny) miny = y; if (y > maxy) maxy = y;
                }
        if (maxx < 0) return b;

        Rectangle cr = new Rectangle(minx, miny, maxx - minx + 1, maxy - miny + 1);
        Bitmap outb = new Bitmap(cr.Width, cr.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(outb))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(b, new Rectangle(0, 0, cr.Width, cr.Height), cr, GraphicsUnit.Pixel);
        }
        b.Dispose();
        return outb;
    }
}
'@

# --- helpers ---------------------------------------------------------------------------
function Save-Png([System.Drawing.Bitmap]$bmp, [string]$path) {
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
}

function Resize-Square([System.Drawing.Bitmap]$square, [int]$size) {
    $b = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.CompositingMode   = 'SourceCopy'
    $g.InterpolationMode  = 'HighQualityBicubic'
    $g.PixelOffsetMode    = 'HighQuality'
    $g.SmoothingMode      = 'HighQuality'
    $g.DrawImage($square, 0, 0, $size, $size)
    $g.Dispose()
    $b
}

function New-Square([System.Drawing.Bitmap]$master, [double]$pad) {
    $side   = [Math]::Max($master.Width, $master.Height)
    $p      = [int][Math]::Round($side * $pad)
    $canvas = $side + 2 * $p
    $b = New-Object System.Drawing.Bitmap $canvas, $canvas
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.CompositingMode   = 'SourceOver'
    $g.InterpolationMode  = 'HighQualityBicubic'
    $g.PixelOffsetMode    = 'HighQuality'
    $g.SmoothingMode      = 'HighQuality'
    $x = [int][Math]::Round(($canvas - $master.Width)  / 2)
    $y = [int][Math]::Round(($canvas - $master.Height) / 2)
    $g.DrawImage($master, $x, $y, $master.Width, $master.Height)
    $g.Dispose()
    $b
}

function New-CheckerPreview([System.Drawing.Bitmap]$square, [int]$size, [int]$cell) {
    $b = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($b)
    $light = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 214, 214, 214))
    $dark  = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 170, 170, 170))
    for ($y = 0; $y -lt $size; $y += $cell) {
        for ($x = 0; $x -lt $size; $x += $cell) {
            $on = ((([int]($x / $cell)) + ([int]($y / $cell))) % 2) -eq 0
            $g.FillRectangle(($(if ($on) { $light } else { $dark })), $x, $y, $cell, $cell)
        }
    }
    $light.Dispose(); $dark.Dispose()
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode   = 'HighQuality'
    $g.SmoothingMode     = 'HighQuality'
    $g.DrawImage($square, 0, 0, $size, $size)
    $g.Dispose()
    $b
}

function Write-Ico([System.Collections.Specialized.OrderedDictionary]$pngMap, [string]$path) {
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $keys = @($pngMap.Keys | Sort-Object { [int]$_ })
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$keys.Count)
    $offset = 6 + 16 * $keys.Count
    foreach ($k in $keys) {
        $bytes = $pngMap[$k]
        $dim   = [byte]($(if ([int]$k -ge 256) { 0 } else { [int]$k }))
        $bw.Write($dim); $bw.Write($dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$bytes.Length); $bw.Write([UInt32]$offset)
        $offset += $bytes.Length
    }
    foreach ($k in $keys) { $bw.Write([byte[]]$pngMap[$k]) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
    $bw.Dispose(); $ms.Dispose()
}

function Png-Bytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $out = $ms.ToArray()
    $ms.Dispose()
    ,$out
}

# --- run ----------------------------------------------------------------------------------
$jobs = @(
    [pscustomobject]@{ Name='light'; Base='historia-light'; Dark=$false; Src=$LightSource
                       FloodKey=$LightFloodKey; RampLo=$LightRampLo; RampHi=$LightRampHi }
    [pscustomobject]@{ Name='dark';  Base='historia-dark';  Dark=$true;  Src=$DarkSource
                       FloodKey=$DarkFloodKey;  RampLo=$DarkRampLo;  RampHi=$DarkRampHi }
)
if ($DarkOnly) { $jobs = @($jobs | Where-Object { $_.Name -eq 'dark' }) }

# macOS iconset: (pixel size, file name)
$iconsetPlan = @(
    @(16,'icon_16x16.png'),   @(32,'icon_16x16@2x.png'),
    @(32,'icon_32x32.png'),   @(64,'icon_32x32@2x.png'),
    @(128,'icon_128x128.png'),@(256,'icon_128x128@2x.png'),
    @(256,'icon_256x256.png'),@(512,'icon_256x256@2x.png'),
    @(512,'icon_512x512.png'),@(1024,'icon_512x512@2x.png')
)

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

foreach ($j in $jobs) {
    Write-Host ""
    Write-Host ("=== {0}  ({1})" -f $j.Base, (Split-Path -Leaf $j.Src)) -ForegroundColor Cyan
    $dir = Join-Path $OutDir $j.Name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null

    $master = [HistoriaLogo]::Extract($j.Src, [bool]$j.Dark, [int]$j.FloodKey, [int]$j.RampLo, [int]$j.RampHi, [bool]$FloodFill)
    Save-Png $master (Join-Path $dir ("{0}-master.png" -f $j.Base))
    Write-Host ("  master   {0}x{1}" -f $master.Width, $master.Height)

    $square = New-Square $master $Padding
    Save-Png $square (Join-Path $dir ("{0}-square.png" -f $j.Base))
    Write-Host ("  square   {0}x{1}" -f $square.Width, $square.Height)

    foreach ($s in ($Sizes | Sort-Object -Unique)) {
        $r = Resize-Square $square $s
        Save-Png $r (Join-Path $dir ("{0}-{1}.png" -f $j.Base, $s))
        $r.Dispose()
    }
    Write-Host ("  png set  {0} sizes: {1}" -f ($Sizes | Sort-Object -Unique).Count, (($Sizes | Sort-Object -Unique) -join ', '))

    $map = New-Object System.Collections.Specialized.OrderedDictionary
    foreach ($s in ($IcoSizes | Sort-Object -Unique)) {
        $r = Resize-Square $square $s
        $map[[string]$s] = (Png-Bytes $r)
        $r.Dispose()
    }
    Write-Ico $map (Join-Path $dir ("{0}.ico" -f $j.Base))
    Write-Host ("  ico      {0}  [{1}]" -f ("{0}.ico" -f $j.Base), (($IcoSizes | Sort-Object -Unique) -join ','))

    $iconset = Join-Path $dir ("{0}.iconset" -f $j.Base)
    New-Item -ItemType Directory -Force -Path $iconset | Out-Null
    foreach ($p in $iconsetPlan) {
        $r = Resize-Square $square ([int]$p[0])
        Save-Png $r (Join-Path $iconset ([string]$p[1]))
        $r.Dispose()
    }
    Write-Host ("  iconset  {0}/  (run: iconutil -c icns {0})" -f ("{0}.iconset" -f $j.Base))

    # app HomeView asset names. ViewKit.cs loads historia-mark-<theme>(@2x).png at 60 / 120.
    Copy-Item (Join-Path $dir ("{0}-60.png"  -f $j.Base)) (Join-Path $dir 'historia-mark.png')                    -Force
    Copy-Item (Join-Path $dir ("{0}-120.png" -f $j.Base)) (Join-Path $dir 'historia-mark@2x.png')                 -Force
    Copy-Item (Join-Path $dir ("{0}-60.png"  -f $j.Base)) (Join-Path $dir ("historia-mark-{0}.png"    -f $j.Name)) -Force
    Copy-Item (Join-Path $dir ("{0}-120.png" -f $j.Base)) (Join-Path $dir ("historia-mark-{0}@2x.png" -f $j.Name)) -Force

    Save-Png (New-CheckerPreview $square 512 16) (Join-Path $dir '_preview-512.png')
    Write-Host "  preview  _preview-512.png"

    $master.Dispose(); $square.Dispose()
}

Write-Host ""
Write-Host "Done. Output tree:" -ForegroundColor Green
Get-ChildItem -Recurse -File $OutDir |
    ForEach-Object { "  " + $_.FullName.Substring($OutDir.Length + 1) } |
    Sort-Object
Write-Host ""
Write-Host "Map into the app:" -ForegroundColor Yellow
Write-Host "  light/historia-mark-light.png  historia-mark-light@2x.png  ->  Historia.App/Assets/"
Write-Host "  dark/historia-mark-dark.png    historia-mark-dark@2x.png   ->  Historia.App/Assets/"
Write-Host "  dark/historia-dark.ico                                     ->  Historia.App/Assets/Historia.ico  (+ repo-root Historia.ico)"
Write-Host "  light/ = slate-blue-on-transparent (light UI),  dark/ = silver-on-transparent (dark UI)"
