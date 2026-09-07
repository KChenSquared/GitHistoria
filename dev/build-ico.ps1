#Requires -Version 5.1
<#
.SYNOPSIS
    Build ONLY the two Windows .ico files for Historia - assets/historia-light.ico and
    assets/historia-dark.ico - from the book-emblem JPEGs.

.DESCRIPTION
    A trimmed copy of dev/extract-logos.ps1: same pixel extraction, same square /
    resample / .ico writer, but it emits nothing except the multi-resolution .ico for
    each theme. No PNG set, no .iconset, no HomeView marks, no preview.

    Extraction (identical to extract-logos.ps1's default): the background is flood-filled
    inward from the image border and keyed to transparency; white / black that the
    artwork ENCLOSES - the rounded tile behind the book - is kept, so the icon is a
    solid tile that reads on any wallpaper. -NoFloodFill keys the field globally instead.

    Sources (under assets/, 880x1213):
      * Historia_book_light.jpg - slate-blue book on near-white  -> historia-light.ico
      * Historia_book_dark.jpg  - silver book on near-black       -> historia-dark.ico

    The dark .ico is the app icon (dev/build-ico.ps1 -Install, or copy it onto
    Historia.App/Historia.ico + the repo-root / Assets mirrors).

.EXAMPLE
    powershell -File dev/build-ico.ps1
.EXAMPLE
    powershell -File dev/build-ico.ps1 -Install        # also set it as the app icon
#>
[CmdletBinding()]
param(
    [string]  $RepoRoot,
    [string]  $OutDir,
    [string]  $LightSource,
    [string]  $DarkSource,

    [int[]]   $IcoSizes     = @(16,24,32,48,64,128,256),
    [double]  $Padding      = 0.06,   # transparent margin around the emblem, as a fraction of its longer side

    # White-background key (min(R,G,B)). Paper ~240-253, emblem body min < 90.
    [int]     $LightFloodKey = 224,   # a pixel this bright (min channel) can be traversed as background
    [int]     $LightRampLo   = 168,   # min channel <= this -> fully opaque
    [int]     $LightRampHi   = 232,   # min channel >= this -> fully transparent

    # Black-background key (max(R,G,B)). Bg ~0-4, emblem body max > 90; faint outer glow ~10-60.
    [int]     $DarkFloodKey  = 42,
    [int]     $DarkRampLo    = 12,    # max channel <= this -> fully transparent
    [int]     $DarkRampHi    = 72,    # max channel >= this -> fully opaque

    [switch]  $NoFloodFill,          # key globally instead of only the border-connected background
    [switch]  $Install               # also copy historia-dark.ico onto the app-icon slots
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $scriptDir }
if (-not $OutDir)   { $OutDir   = Join-Path $RepoRoot 'assets' }

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

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

foreach ($j in $jobs) {
    Write-Host ""
    Write-Host ("=== {0}  ({1})" -f $j.Base, (Split-Path -Leaf $j.Src)) -ForegroundColor Cyan

    $master = [HistoriaLogo]::Extract($j.Src, [bool]$j.Dark, [int]$j.FloodKey, [int]$j.RampLo, [int]$j.RampHi, (-not $NoFloodFill))
    $square = New-Square $master $Padding

    $map = New-Object System.Collections.Specialized.OrderedDictionary
    foreach ($s in ($IcoSizes | Sort-Object -Unique)) {
        $r = Resize-Square $square $s
        $map[[string]$s] = (Png-Bytes $r)
        $r.Dispose()
    }
    $path = Join-Path $OutDir ("{0}.ico" -f $j.Base)
    Write-Ico $map $path
    Write-Host ("  {0}  [{1}]" -f $path, (($IcoSizes | Sort-Object -Unique) -join ','))

    $master.Dispose(); $square.Dispose()
}

if ($Install) {
    $darkIco = Join-Path $OutDir 'historia-dark.ico'
    $targets = @(
        Join-Path $RepoRoot 'Historia.App\Historia.ico'        # <ApplicationIcon>, embedded in Historia.exe
        Join-Path $RepoRoot 'Historia.ico'                     # repo-root mirror
        Join-Path $RepoRoot 'Historia.App\Assets\Historia.ico' # avares mirror
    )
    Write-Host ""
    foreach ($t in $targets) {
        Copy-Item -LiteralPath $darkIco -Destination $t -Force
        Write-Host ("  installed  {0}" -f $t.Substring($RepoRoot.Length + 1)) -ForegroundColor Green
    }
    Write-Host ""
    Write-Host "Rebuild Historia.App for the new icon to embed in Historia.exe." -ForegroundColor Yellow
}
