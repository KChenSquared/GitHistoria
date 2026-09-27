# Historia 📜

> *The commit graph you love, without the subscription.*

A high-performance, open-source Windows Git client built with **C# (.NET 10)** and **Avalonia UI**.

---

## 💡 Why Historia?

I've used GitKraken as my primary Git tool for many years. I love the way it presents the
commit graph; it's extremely useful for keeping up with my repos. Over time, though, it became extremely sluggish on the repos I work in day
to day. Each one has 200k–350k commits, and switching between them had become a very poor
experience. Then my annual GitKraken renewal came around and the price was going up again.

So I set out to build a free, better-performing alternative to the commit graph and the core Git features I
rely on. The graph keeps the same easy-to-read orthogonal edge routing, but it can render
repos with hundreds of thousands of commits and scroll through them quickly and smoothly.

---

## 🚀 Vision & Mission

Our goal is to create the premier Windows Git GUI that unites:
1. **Raw Speed and Responsiveness** — instant launch, lightweight memory usage, zero UI lag, and smooth handling of massive repositories (100k+ commits).
2. **Feature Completeness & Simplicity** — advanced power tools (interactive rebase\*, line/hunk staging, cherry-pick, submodules, stash management) paired with an intuitive, clean workflow for everyday commits, branching, and conflict resolution.
3. **Visual Elegance** — vivid, color-coded branch lanes, round commit nodes, branch/tag labels, merge arcs, and visual drag-and-drop actions — engineered for high-performance rendering.
4. **Progressive Disclosure** — essential, everyday workflows (inspecting history, staging, committing, branch switching) remain immediate and distraction-free, while deep Git power tools (interactive rebase\*, conflict resolver, hunk staging, submodules, worktrees, terminal, error diagnostics) disclose on demand through context menus, collapsible drawers, and dedicated inspection panels without overwhelming the user.

---

## 💎 Core Pillars

- **Confirm Before Destroying Anything:** The project's highest rule (constitution Rule 00). No
  action that discards uncommitted work or rewrites history — discard, clean, hard reset,
  branch/tag delete, rebase, force-push, stash drop — runs without an explicit modal alert
  (red warning icon, exact effect named) whose **Cancel** button is the highlighted default.
- **Performance-First Architecture:** Powered by native .NET and Avalonia UI; minimal memory footprint, non-blocking Git operations, and fast cold starts (< 800ms).
- **Progressive Disclosure UI:** A layered interface philosophy ensuring zero visual clutter. Common daily operations take center stage with high ergonomic polish; advanced Git machinery and deep technical metadata remain readily accessible but stay tucked behind collapsible sections, contextual actions, and detail drawers until needed.
- **Next-Gen Commit Graph:** Virtualized rendering (60+ FPS) designed to scroll smoothly across repositories with hundreds of thousands of commits. Features vibrant multi-color branch lanes and orthogonal edge routing with rounded corners.
- **Power-User Feature Parity:** 100% core Git autonomy, visual interactive rebase\*, granular staging, 3-way conflict resolver, and full repository management (submodules, worktrees, LFS).
- **Tri-State Network Policy:**
  - **Quiet Network (Local/SSH):** Zero background API polling, telemetry, or avatar leaks.
  - **Hybrid (Default):** Seamlessly supports SSH and HTTPS.
  - **OAuth Only:** Manages all credentials via browser OAuth with rich integrations.

<sub>\* On the [roadmap](docs/roadmap.md).</sub>

---

## 📚 Documentation

- 📐 [**Comprehensive Design Documentation**](Historia-DevDocs/ComprehensiveDesign/README.md) — Technical architecture, Git data access strategy, and DAG graph layout engine.
- 📜 [**Historia Constitution**](docs/constitution.md) — Architectural invariants, security policies, and performance ceilings.
- 🗺️ [**Version Plans**](Historia-DevDocs/versionPlans/) — The active milestone's plan. Older version plans are moved into an offline archive to keep the working set small and prevent context drift; `ComprehensiveDesign/` is the living spec.
- 🛣️ [**Roadmap**](docs/roadmap.md) — Planned features for upcoming releases.
- 🤝 [**Contributing Guide**](docs/contributing.md) — Environment setup, coding standards, PR guidelines, and core tenets.

---

## ⚡ Quick Start

### Prerequisites
- Windows 10 (1809+) or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)

### Build & Run
```powershell
# Restore and build
dotnet build

# Launch the application
dotnet run

# Launch ScenarioLab (test scenario generator & runner)
dotnet run --project Historia.ScenarioLab
```

### macOS: ad-hoc signing

macOS (especially Apple Silicon) refuses to launch an unsigned app bundle. If you
don't have an Apple Developer certificate, sign the build locally with an *ad-hoc*
signature (the `-` identity). This produces a binary that runs on **your own
machine** only — it is not distributable and won't pass notarization.

```bash
# 1. Publish a framework-dependent build for your architecture
#    (use osx-x64 on Intel Macs)
dotnet publish Historia.App/Historia.App.csproj -c Release -r osx-arm64 --self-contained false

PUB=Historia.App/bin/Release/net10.0/osx-arm64/publish

# 2. Ad-hoc sign the main executable and every native dylib it ships
codesign --force --deep --sign - --timestamp=none "$PUB/Historia"
find "$PUB" -name "*.dylib" -exec codesign --force --sign - {} \;

# 3. Strip the quarantine flag Gatekeeper adds to downloaded files
xattr -dr com.apple.quarantine "$PUB"

# 4. Run it
"$PUB/Historia"
```

If macOS still blocks the app, open **System Settings → Privacy & Security** and
click **Open Anyway**, or run `spctl --add "$PUB/Historia"`.

When packaged as a `Historia.app` bundle, sign the bundle root instead:

```bash
codesign --force --deep --sign - Historia.app
xattr -dr com.apple.quarantine Historia.app
```

---

## 📄 License

Historia is free software: you can redistribute it and/or modify it under the terms of
the [GNU General Public License v3.0](LICENSE) as published by the Free Software
Foundation, either version 3 of the License, or (at your option) any later version.

It is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without
even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See
the [GNU General Public License](LICENSE) for more details.

Copyright (C) 2026 The Historia Contributors.

Bundled third-party material (the [Material Symbols](https://github.com/google/material-design-icons) icon set, Apache-2.0; the diff viewer and conflict editor adapted from [SourceGit](https://github.com/sourcegit-scm/sourcegit), MIT; and editor libraries AvaloniaEdit, TextMateSharp, onigwrap, and DiffPlex) is credited
in [`docs/third-party-licenses.md`](docs/third-party-licenses.md).
