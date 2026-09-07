# Third-party licences

Historia bundles the following third-party material as source (no CDN, nothing fetched
at build or run time — constitution Rule 0).

---

## Lucide

The utility icon set in [`Historia.Core/Components/IconPaths.cs`](../Historia.Core/Components/IconPaths.cs)
(and its generated partial `IconPaths.Generated.cs`) is drawn from the
[Lucide](https://lucide.dev/) icon library, release **v0.544.0**. The glyph SVGs are
vendored under [`assets/lucide/`](../assets/lucide/) and flattened into path data by
[`dev/vendor-lucide.ps1`](../dev/vendor-lucide.ps1). Icons are re-drawn as
monochrome `currentColor` strokes; no Lucide code is shipped.

```
ISC License

Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2022 as part of
Feather (MIT). All other copyright (c) for Lucide are held by Lucide Contributors
2022.

Permission to use, copy, modify, and/or distribute this software for any purpose
with or without fee is hereby granted, provided that the above copyright notice
and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH
REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS
OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER
TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF
THIS SOFTWARE.
```
