# Historia 📜

> *The commit graph you love: every commit, no lag, no subscriptions.*

A high-performance, open-source desktop Git client built with **C# (.NET 10)** and
**Avalonia UI**. Windows is the primary platform and macOS is supported; other platforms
will follow.

---

## 💡 Why Historia?

GitKraken has been my go-to Git client for both work and personal projects for years,
mainly because its commit graph is so great for staying updated and sometimes to remind me
what I was doing. Unfortunately, as the repos I worked on grew in size, GitKraken became
more sluggish. Once they hit that 100k+ commit mark, switching between repos and basic git
functions was a total headache. To top it off, my subscription renews next month and the
cost is doubling from last year — plus it's going to jump again the year after.

On top of that, I started my agentic coding journey this year, and it quickly became
apparent that keeping track of all the work happening in parallel would be crucial in the
age of rapid prototyping with AI.

So I decided to put my Claude and Gemini subscriptions to use and build a free,
fast alternative to the commit graph I've come to rely on. Historia's graph keeps the
same easy-to-read orthogonal edge routing, but it can render repos with hundreds of
thousands of commits and scroll through them quickly and smoothly. Beyond the graph,
Historia is built with agentic workflows in mind: a command center lets your agents point
you to what needs your review.

**Trade-offs.** Historia loads your entire commit history instead of capping it at the
most recent few thousand commits. That means memory grows with the size of the repo:
about 200 MB for a 350k-commit repo, and about 2 GB for Chromium's ~1.8 million commits.
In return, the whole history is in one graph, and scrolling through it stays smooth. A
lightweight mode that caps the graph at 30k commits for lower memory use is available in
settings.

---

## 🚀 Vision & Mission

My goal is to provide a desktop GUI with a fast and informative graph, available to all
users on all repos. It is built around:

1. **Raw Speed and Responsiveness** — fast launch, zero UI lag, and smooth handling of
   massive repositories (hundreds of thousands of commits), keeping the full history
   loaded rather than capping it.
2. **Feature Completeness & Simplicity** — advanced power tools (interactive rebase\*,
   line/hunk staging, cherry-pick, submodules, stash management) paired with an
   intuitive, clean workflow for everyday commits, branching, and conflict resolution.
3. **Visual Elegance** — vivid, color-coded branch lanes, round commit nodes, branch/tag
   labels, merge arcs, and visual drag-and-drop actions\* — engineered for
   high-performance rendering.
4. **Progressive Disclosure** — essential, everyday workflows (inspecting history,
   staging, committing, branch switching) remain immediate and distraction-free, while
   deep Git power tools (interactive rebase\*, conflict resolver, hunk staging,
   submodules, worktrees, terminal, error diagnostics) disclose on demand through context
   menus, collapsible drawers, and dedicated inspection panels without overwhelming the
   user.

---

## 💎 Core Pillars

- **Confirm Before Destroying Anything:** The project's highest rule (constitution Rule 00). No
  action that discards uncommitted work or rewrites history — discard, clean, hard reset,
  branch/tag delete, rebase, force-push, stash drop — runs without an explicit modal alert
  (red warning icon, exact effect named) whose **Cancel** button is the highlighted default.
- **Performance-First Architecture:** Powered by native .NET and Avalonia UI; non-blocking
  Git operations, fast cold starts, and memory that scales with history size (see
  [Trade-offs](#-why-historia)).
- **Progressive Disclosure UI:** A layered interface philosophy ensuring zero visual
  clutter. Common daily operations take center stage with high ergonomic polish; advanced
  Git machinery and deep technical metadata remain readily accessible but stay tucked
  behind collapsible sections, contextual actions, and detail drawers until needed.
- **Next-Gen Commit Graph:** Virtualized rendering (60+ FPS) designed to scroll smoothly
  across repositories with hundreds of thousands of commits. Features vibrant multi-color
  branch lanes and orthogonal edge routing with rounded corners.
- **Power-User Feature Parity:** 100% core Git autonomy, visual interactive rebase\*,
  granular staging, 3-way conflict resolver, and full repository management (submodules,
  worktrees, LFS\*).
- **SSH Only Mode:** By default, fetch, pull, and push try the remote's own scheme first
  and fall back between SSH and HTTPS. Switch a repo to SSH Only to force every network
  operation over SSH with no HTTPS fallback.

<sub>\* On the [roadmap](docs/roadmap.md).</sub>

---

## 📚 Documentation

- 📐 [**Comprehensive Design Documentation**](Historia-DevDocs/ComprehensiveDesign/README.md)
  — Technical architecture, Git data access strategy, and DAG graph layout engine.
- 📜 [**Historia Constitution**](docs/constitution.md) — Architectural invariants, security
  policies, and performance ceilings.
- 🗺️ [**Version Plans**](Historia-DevDocs/versionPlans/) — The active milestone's plan.
  Older version plans are moved into an offline archive to keep the working set small and
  prevent context drift; `ComprehensiveDesign/` is the living spec.
- 🛣️ [**Roadmap**](docs/roadmap.md) — Planned features for upcoming releases.
- 🤝 [**Contributing Guide**](docs/contributing.md) — Environment setup, coding standards,
  PR guidelines, and core tenets.

---

## 📋 Requirements

- **Windows** (primary): Windows 10 (1809+) or Windows 11
- **macOS** (secondary): Apple Silicon or Intel; builds must be ad-hoc signed locally (see
  [macOS: ad-hoc signing](#macos-ad-hoc-signing))
- [.NET 10 Runtime](https://dotnet.microsoft.com/download) (the Desktop Runtime on
  Windows)
- [Git](https://git-scm.com/downloads) **2.46 or newer**. Historia checks the installed
  version at startup and reports it if Git is missing or too old.

---

## ⚡ Quick Start

### Prerequisites

Everything under [Requirements](#-requirements), plus the
[.NET 10 SDK](https://dotnet.microsoft.com/download) to build from source.

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

Bundled third-party material is credited in
[`docs/third-party-licenses.md`](docs/third-party-licenses.md): the
[Material Symbols](https://github.com/google/material-design-icons) icon set (Apache-2.0);
the diff viewer and conflict editor adapted from
[SourceGit](https://github.com/sourcegit-scm/sourcegit) (MIT); editor libraries
AvaloniaEdit, TextMateSharp, onigwrap, and DiffPlex; and terminal libraries XTerm.NET,
Porta.Pty, and Microsoft.Windows.Console.ConPTY.
