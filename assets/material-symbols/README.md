# Material Symbols (Outlined)

This directory vendors the SVG glyphs used by Historia, sourced from Google's [Material Symbols](https://github.com/google/material-design-icons).

## Provenance
- **Source repository:** `google/material-design-icons`
- **Pinned commit:** `40a7a292a79d9394157e1ea24f83d52d5e17c556` (2026-09-11)
- **Source path:** `symbols/web/<name>/materialsymbolsoutlined/<name>_24px.svg`
- **Style parameters:**
  - Variant: **Outlined**
  - Weight: **400** (`wght` 400)
  - Grade: **0** (`GRAD` 0)
  - Fill: **0** (`FILL` 0)
  - Optical size: **24** (`opsz` 24)
- **Licence:** Apache License 2.0 (Copyright Google LLC). See [docs/third-party-licenses.md](../../docs/third-party-licenses.md) for the full licence text and details.

## How Glyphs Are Used
The SVGs checked in here are flattened into C# path-data strings in `Historia.Core/Components/IconPaths.Generated.cs` by running `dev/vendor-material-symbols.ps1`.
At runtime, glyphs are rendered in `currentColor` as monochrome fill geometries scaled from Material's 960-unit grid (`viewBox 0 -960 960 960`). No runtime network requests or font files are used (constitution Rule 0 / Rule 0.1).

## Adding a Glyph
1. Find the desired glyph name in Google Material Symbols at the pinned commit (`40a7a292a79d9394157e1ea24f83d52d5e17c556`).
2. Download `symbols/web/<name>/materialsymbolsoutlined/<name>_24px.svg` into this directory as `<name>.svg`.
3. Verify the file contains a single `<path>` element on `viewBox="0 -960 960 960"`.
4. Run `dev/vendor-material-symbols.ps1` from the repository root to regenerate `IconPaths.Generated.cs`.
5. Update `docs/third-party-licenses.md` if any licensing metadata changes.
