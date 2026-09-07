# Vendored Lucide sources

These SVGs are a checked-in copy of selected [Lucide](https://lucide.dev/) glyphs,
pinned to release **v0.544.0** (ISC licence — see `docs/third-party-licenses.md`).

`dev/vendor-lucide.ps1` reads every `*.svg` here and regenerates
`Historia.Core/Components/IconPaths.Generated.cs`. Nothing is fetched at build or run
time (constitution Rule 0). To add a glyph: drop its `<name>.svg` from the pinned
Lucide release here and re-run the script.
