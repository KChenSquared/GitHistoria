#!/usr/bin/env pwsh
# Regenerates Historia.Core/Components/IconPaths.Generated.cs from the checked-in
# Lucide SVG sources under assets/lucide/.  Nothing is fetched from the network
# (constitution Rule 0) — the SVGs are vendored, pinned to the release below.
#
#   Lucide release : v0.544.0   (https://lucide.dev/  — ISC licence)
#   Licence text   : docs/third-party-licenses.md
#
# Every glyph is flattened to a single path-data string on the Lucide 24x24 grid
# and rendered stroke-based (currentColor) by ViewKit.IconGlyphPath.  Re-running
# this script on an unchanged assets/lucide/ produces no diff (idempotent).

$ErrorActionPreference = 'Stop'
$LucideVersion = 'v0.544.0'

$root    = Split-Path -Parent $PSScriptRoot
$srcDir  = Join-Path $root 'assets/lucide'
$outFile = Join-Path $root 'Historia.Core/Components/IconPaths.Generated.cs'

function Format-Num([double]$v) {
    $r = [math]::Round($v, 4)
    return $r.ToString([System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-Attr([string]$tag, [string]$name) {
    $m = [regex]::Match($tag, "$name\s*=\s*""([^""]*)""")
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

function Convert-Primitive([string]$tag) {
    $el = ([regex]::Match($tag, '^<\s*([a-zA-Z]+)')).Groups[1].Value
    switch ($el) {
        'path' {
            return (Get-Attr $tag 'd')
        }
        'line' {
            $x1 = [double](Get-Attr $tag 'x1'); $y1 = [double](Get-Attr $tag 'y1')
            $x2 = [double](Get-Attr $tag 'x2'); $y2 = [double](Get-Attr $tag 'y2')
            return "M$(Format-Num $x1) $(Format-Num $y1)L$(Format-Num $x2) $(Format-Num $y2)"
        }
        'polyline' {
            $pts = (Get-Attr $tag 'points').Trim() -split '[\s,]+'
            $sb = "M$($pts[0]) $($pts[1])"
            for ($i = 2; $i -lt $pts.Count; $i += 2) { $sb += "L$($pts[$i]) $($pts[$i+1])" }
            return $sb
        }
        'circle' {
            $cx = [double](Get-Attr $tag 'cx'); $cy = [double](Get-Attr $tag 'cy')
            $r  = [double](Get-Attr $tag 'r')
            return "M$(Format-Num ($cx - $r)) $(Format-Num $cy)" +
                   "a$(Format-Num $r) $(Format-Num $r) 0 1 0 $(Format-Num (2 * $r)) 0" +
                   "a$(Format-Num $r) $(Format-Num $r) 0 1 0 $(Format-Num (-2 * $r)) 0"
        }
        'rect' {
            $x = [double](Get-Attr $tag 'x'); $y = [double](Get-Attr $tag 'y')
            $w = [double](Get-Attr $tag 'width'); $h = [double](Get-Attr $tag 'height')
            $rxRaw = Get-Attr $tag 'rx'
            $rx = if ($rxRaw) { [double]$rxRaw } else { 0 }
            if ($rx -le 0) {
                return "M$(Format-Num $x) $(Format-Num $y)h$(Format-Num $w)v$(Format-Num $h)h$(Format-Num (-$w))z"
            }
            $wi = $w - 2 * $rx; $hi = $h - 2 * $rx
            return "M$(Format-Num ($x + $rx)) $(Format-Num $y)" +
                   "h$(Format-Num $wi)a$(Format-Num $rx) $(Format-Num $rx) 0 0 1 $(Format-Num $rx) $(Format-Num $rx)" +
                   "v$(Format-Num $hi)a$(Format-Num $rx) $(Format-Num $rx) 0 0 1 $(Format-Num (-$rx)) $(Format-Num $rx)" +
                   "h$(Format-Num (-$wi))a$(Format-Num $rx) $(Format-Num $rx) 0 0 1 $(Format-Num (-$rx)) $(Format-Num (-$rx))" +
                   "v$(Format-Num (-$hi))a$(Format-Num $rx) $(Format-Num $rx) 0 0 1 $(Format-Num $rx) $(Format-Num (-$rx))z"
        }
        default { return $null }
    }
}

$entries = [ordered]@{}
foreach ($file in Get-ChildItem -Path $srcDir -Filter '*.svg' | Sort-Object Name) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
    $svg  = Get-Content -Raw -Path $file.FullName
    $parts = @()
    foreach ($m in [regex]::Matches($svg, '<\s*(path|line|polyline|circle|rect)\b[^>]*>')) {
        $d = Convert-Primitive $m.Value
        if ($d) {
            $d = $d.Trim()
            # Each primitive's own "d" is a standalone path-data string, where SVG treats a
            # leading lowercase "m" as absolute (the "first moveto is always absolute" rule) —
            # but critically, per spec, coordinate pairs AFTER that first one stay relative
            # ("subsequent pairs of coordinates are treated as relative even though the initial
            # moveto is interpreted as an absolute moveto"). Joining several primitives' "d"
            # into one combined string for a single glyph loses the absolute-first-moveto part:
            # a later primitive's leading "m" is no longer the first command overall, so it gets
            # reinterpreted as relative to wherever the previous primitive's pen ended up —
            # sending that whole subpath miles off the 24x24 grid (this is what broke arrow-down
            # / arrow-down-to-line: v8.1). A naive fix of just flipping "m" to "M" breaks the
            # OTHER half of that rule instead: it silently turns every implicit lineto pair
            # after the moveto from relative to absolute too, corrupting single-primitive icons
            # like chevron-down that were never joined with anything (v8.1 follow-up fix). The
            # correct transform makes both halves of the rule explicit: "M x y l ..." — an
            # absolute moveto followed by an explicit relative lineto for whatever coordinate
            # pairs originally followed the "m", so an icon with an M/l split renders bit-for-
            # bit the same whether the primitive is the first command in the glyph or the tenth.
            # -cmatch: case-SENSITIVE. The "absolute moveto but still-relative continuation
            # pairs" exception is specific to a lowercase "m" used as the first command of a
            # path (SVG spec). A primitive already starting with uppercase "M" has no such
            # exception — its implicit continuation pairs are already (correctly) absolute,
            # matching general implicit-lineto behaviour, and rewriting them here would flip
            # them to relative and corrupt icons like "check" that were never broken.
            if ($d -cmatch '^m\s*(-?\d*\.?\d+)[,\s]+(-?\d*\.?\d+)(.*)$') {
                # Capture out of $matches before the -notmatch below re-assigns it out from
                # under us — a second -match/-notmatch always overwrites $matches, even one
                # used only in the condition of the very statement that still needs the first
                # match's groups.
                $mx = $matches[1]; $my = $matches[2]; $rest = $matches[3]
                $d = if ($rest.Length -gt 0 -and $rest -notmatch '^[A-Za-z]') { "M$mx $my" + 'l' + $rest }
                     else { "M$mx $my$rest" }
            }
            $parts += $d
        }
    }
    if ($parts.Count -eq 0) { throw "No drawable primitives in $($file.Name)" }
    $entries[$name] = ($parts -join ' ')
}

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('// <auto-generated>')
[void]$sb.AppendLine("//   Generated by dev/vendor-lucide.ps1 from assets/lucide/*.svg")
[void]$sb.AppendLine("//   Lucide $LucideVersion (ISC) - see docs/third-party-licenses.md. Do not edit by hand.")
[void]$sb.AppendLine('// </auto-generated>')
[void]$sb.AppendLine()
[void]$sb.AppendLine('namespace Historia.Components;')
[void]$sb.AppendLine()
[void]$sb.AppendLine('public static partial class IconPaths')
[void]$sb.AppendLine('{')
[void]$sb.AppendLine('    /// <summary>Flattened Lucide glyph geometry (24x24 grid, stroke, currentColor).</summary>')
[void]$sb.AppendLine('    internal static readonly IReadOnlyDictionary<string, string> LucideGlyphs =')
[void]$sb.AppendLine('        new Dictionary<string, string>(System.StringComparer.Ordinal)')
[void]$sb.AppendLine('        {')
foreach ($k in $entries.Keys) {
    $v = $entries[$k].Replace('"', '\"')
    [void]$sb.AppendLine("            [`"$k`"] = `"$v`",")
}
[void]$sb.AppendLine('        };')
[void]$sb.AppendLine('}')

$new = $sb.ToString() -replace "`r`n", "`n"
$existing = ''
if (Test-Path $outFile) { $existing = (Get-Content -Raw $outFile) -replace "`r`n", "`n" }
# [string]::Equals(..., Ordinal), not -eq: PowerShell's -eq does a case-insensitive string
# compare by default, which silently masked a case-only diff here (v8.1 — see the leading-
# moveto fix above) and left the generated file stale even after that fix.
if ([string]::Equals($new, $existing, [System.StringComparison]::Ordinal)) {
    Write-Host "IconPaths.Generated.cs already up to date ($($entries.Count) glyphs)."
} else {
    Set-Content -Path $outFile -Value $new -NoNewline -Encoding utf8
    Write-Host "Wrote $outFile ($($entries.Count) glyphs)."
}
