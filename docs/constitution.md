# Historia — Constitution

The invariants. Every one of these was learned the hard way, from a real defect or a real
design decision. The `(§I-…)`, `(§V2-…)`, and `(§V3-…)` citations point into the initial, v2,
and v3 design documents respectively; those are archived version plans that are rotated out of
this repo, so the tags are kept as provenance markers rather than links. The living design is
the split set under [`Historia-DevDocs/ComprehensiveDesign/`](../Historia-DevDocs/ComprehensiveDesign/).

**A rule here outranks convenience, consistency with other code, and the obvious
implementation.** If a rule blocks you, raise it — do not route around it. If you break one
deliberately, say so in the PR and say why.

---

## Rule 0 — Offline first, always

> **Historia must start and render completely, every time, on a machine with no network — from
> what ships in the package, plus what the Windows platform itself already provides.**

This is the primary rule because it is the one that fails silently: nothing crashes, the app
just quietly reaches out. Everything below is subordinate to it.

**0.1 — No external assets.** No CDN fonts, icon fonts, stylesheets, scripts, or images. Every
   `url()`, `src`, and `href` in the UI resolves to a file in the package. *(§V2-10)*
**0.2 — No network call the user did not initiate.** No telemetry, no analytics, no crash
   reporting, no avatar lookups. Background polling and hosting-API calls run only while a
   profile with a connected token is the active per-repo selection; a repo with its **SSH Only**
   toggle on makes no non-SSH network call and falls closed rather than back to HTTPS.
   **Auto-fetch** (§12.1) is the one sanctioned exception to "only what the user initiated": while
   `AutoFetchEnabled` is on (default on) it periodically `git fetch`es every open repo over its
   configured transport, regardless of profile/token. Opening a repo with a remote is treated as
   the consent; the setting is the opt-out, and it must stay a single visible toggle that turns
   *all* of it off — never a call that cannot be disabled. *(§I-5.5, §12.1, §13)*
**0.3 — Three tiers, and only the third is negotiable.**
   - **Preinstalled platform — assume it.** Present on a stock Windows 11 machine: **Segoe UI**
     and the **Win32/DWM APIs**.
   - **Declared prerequisites — permitted, but they must be declared.** Not preinstalled, and
     legitimately required: the **.NET Desktop Runtime**. Permitted only when the installer
     installs it, the requirement is documented for anyone running raw binaries, and rule 0.4's
     check reports its absence rather than crashing. Adding a new prerequisite is a deliberate
     decision, not something a `csproj` edit does by accident.
   - **Everything else — bundle it.** Fonts, icons, syntax highlighters, markdown renderers,
     brand assets. When it is arguable, bundle. *(§V2-10.3)*

     **"Bundle" means ship it, not write it.** A NuGet package that ships inside the package and
     makes no network call satisfies this rule completely. Rule 0.1 governs runtime asset
     *fetches*; it says nothing about compile-time dependencies. Hand-rolling a highlighter, a
     renderer, or a differ is a choice to be argued on its own merits — coverage, licence,
     attack surface — and this rule never requires it. Note that the merits sometimes favour
     hand-rolling: `MarkdownRenderer` encodes every source character before adding markup, so
     raw HTML from a cloned repo's README cannot survive, which a default Markdig pipeline does
     not give you. *(§V3-18.1 D13)*
**0.4 — Assume the baseline, but verify it — never silently repair it.** Anything outside the
   package is detected at startup and, when absent, reported with an actionable message naming
   what is missing and how to get it. The app still starts where it can. Never a crash, never a
   silent download. Applies to `git.exe` and the .NET Desktop Runtime. *(§I-6.2.3, §V2-10.3)*
**0.5 — Windows 10 is supported but is not the baseline.** The TFM floor is 1809. A dependency
   guaranteed on Windows 11 may legitimately be missing there, which is exactly what rule 0.4
   exists to handle gracefully.
**0.6 — Guard it with a test, not a convention — and guard the whole codebase.**
   `OfflineAssetTests` fails the build if an `http(s)` URL reappears in any `.cs` or `.axaml`
   file. Historia is majority C#, so a scan that skips the C# layer guards the half least likely
   to break the rule. Consented exceptions are named in an explicit allowlist; a blind spot is
   not an allowlist. *(§V2-10.4, §V3-18.2 Q8)*
**0.7 — Pin every dependency.** No floating version wildcards.

---

## Git correctness

6. **`LibGit2Sharp.Repository` is not thread-safe.** One instance per `RepositoryContext`,
   every access through its single-slot gate, never on the UI thread. *(§I-6.3.1)*
7. **Nothing LibGit2Sharp owns escapes the gate.** Project into Historia's own records inside
   the callback. A `Commit`, `Tree`, `Blob`, or `Signature` outliving the gate is the most
   likely source of a native crash. *(§I-6.3.1)*
8. **LibGit2Sharp never touches the network.** It has no access to `ssh-agent`,
   `GIT_SSH_COMMAND`, or GCM, so its transport would bypass the entire auth and privacy
   model. Clone, fetch, pull, push are always CLI. *(§I-6.4.1)*
9. **CLI arguments go through `ArgumentList`.** Never a concatenated string, never a shell.
   Paths after `--` as literal pathspecs. *(§I-6.5.1)*
10. **Drain stdout and stderr concurrently before `WaitForExit`.** The other order deadlocks
    once output exceeds the pipe buffer. *(§I-6.5.1)*
11. **Raw stderr is never shown.** Classify into `GitErrorKind`; `Unknown` surfaces verbatim
    behind a Details expander. Never invent a silent fallback for an unrecognized failure.
    *(§I-6.5.3)*
12. **Force-push is `--force-with-lease --force-if-includes`.** Never plain `--force`; the
    lease alone is trivially satisfiable after a background fetch. *(§I-6.13)*
13. **Never auto-accept an unknown SSH host key.** *(§I-6.13)*
14. **Destructive actions confirm, naming the exact files.** Discard, delete-untracked, hard
    reset. The data is unrecoverable through Git. *(§I-6.9.1)*
15. **Route commits through the CLI when hooks or signing are present.** LibGit2Sharp runs
    neither, and that discrepancy silently breaks `husky` and `pre-commit`. *(§I-6.9.2)*
16. **Paths are repo-relative with forward slashes.** Convert only at the filesystem
    boundary. *(§I-6.8.1)*
17. **History is always paged.** No call materializes an unbounded commit list. *(§I-6.4.2)*

---

## State & threading

18. **Mutate on background threads, raise events on the UI thread** — `IUiDispatcher` over
    Avalonia's `Dispatcher.UIThread`. Producers never touch the UI. *(§I-6.7)*
19. **Every subscriber is `IDisposable` and unsubscribes.** Undisposed handlers on a
    long-lived state object are the primary leak vector. *(§I-6.7)*
20. **No `AddScoped`.** Nothing creates a DI scope, so a scoped registration behaves as a
    singleton and silently breaks multi-tab isolation. Per-repo services are constructed and
    disposed by `WorkspaceService`. *(§I-6.3)*
21. **Cross-surface communication goes through `IRepositoryStateService`.** Never through a
    parent callback. *(§I-6.7, §V2-8.3)*

---

## Rendering & performance

22. **Never block the UI thread.** A 2 s budget is a correctness ceiling, not licence to
    block; show a skeleton after ~200 ms. *(§I-3)*
23. **Anything long is virtualized** — graph rows, diff lines. Only visible content plus
    overscan exists. *(§I-6.10.3, §I-7.4)*
24. **The graph render path creates no Historia-owned object per frame — and a test says so.**
    Pool every reusable `Pen`, `Brush`, and `Geometry`, clip geometries included; cache
    `FormattedText` with every glyph-affecting input in the key. **"None" is literal and it
    is measured** — but measured as *pool and cache misses*, never as bytes: every pool and cache
    carries a miss counter, and a render pass after the first must record zero. Counting bytes
    cannot work here — the rendering stack allocates its own per-frame buffers however correct
    the pooling is, so a frame-to-frame byte delta stays perfectly flat while a per-frame `new`
    goes on happening, and a gate that passes while the rule is broken is worse than none.
    *(§V3-18.1 D14)* This rule stood unmeasured from Phase 4 to Phase 8 and was
    violated the whole time in three places — per-pill pens and brushes, per-column clip
    geometries, per-column header text — with nobody noticing, because an absolute nobody checks
    is decoration. *(§I-7.4, §V2-6.3, §V3-2.1)*
25. **45+ FPS sustained, no frame over 22.2 ms.** The ceiling is the meaningful half — an
    average hiding 100 ms hitches still feels broken. **This one cannot be a build gate** —
    frame timing on a shared runner measures the runner, not Historia. It is a manual benchmark
    against a recorded baseline, re-run whenever the render path changes, with the number
    written into that phase's implementation log. A performance rule with neither a check nor a
    recorded measurement is an aspiration; the rule must say which of the two it is.
    *(§I-3, §V3-17.2)*

---

## UI & theming

26. **One palette, kept honest by a test.** `HistoriaPalette` is the single source of truth;
    `AvaloniaThemeApplier` generates the resource dictionary from it, and `PaletteParityTests`
    checks that every theme defines exactly the declared token set. **No colour literal
    anywhere else.** *(§V2-3)*
27. *(Removed.)*
28. **Theme changes are live.** No restart, no control rebuild, no flash —
    `AvaloniaThemeApplier` swaps the merged resource dictionary in place. *(§I-11.3)*
29. **Icons are monochrome silhouettes in `currentColor`.** Colour is reserved for meaning:
    file status, diff lines, lane colours, notification severity. *(§V2-9.2)*
30. **Git works with zero accounts.** Every core feature is fully local; remote API panels
    collapse quietly when unconfigured. No nag screens, no forced login. *(§I-5.3, §I-5.4)*
30.1. **Progressive disclosure across all UI surfaces.** Primary views remain clean, fast, and
    uncluttered for everyday workflows (inspecting history, staging, committing, branch
    switching). Advanced machinery and dense metadata are revealed on demand:
    - **Surfaces & panels:** Advanced tools (embedded terminal, interactive rebase, 3-way conflict
      resolver, worktrees, submodules) live in collapsible drawers or routed views, never
      permanently crowding the primary canvas (§3, §11, §11A).
    - **Hierarchies & lists:** Multi-level structures (such as ref folders in the sidebar) default
      deeper levels to collapsed on startup, deferring expensive enumeration until expanded (§8).
    - **Errors & diagnostics:** Faults present classified, human-friendly summaries first; raw Git
      stderr and technical traces are sequestered behind an explicit Details expander (Rule 11, §18).
    - **Graph & badges:** Clustered ref labels fold into consolidated primary badges with `+N`
      sibling overflow chips revealing detail on hover or click (§7.2).
    - **Settings:** Advanced toggles and terminal preferences sit in collapsible sections, keeping
      the initial settings view focused (§16).

---

## Secrets

31. **Secrets live in Windows Credential Manager.** `profiles.json` stores reference keys;
    profiles store SSH key *paths* and GPG key *ids*, never key material. *(§I-12)*
32. **SSH keys are per-process environment, never process-global.** Two tabs on two profiles
    must be able to push concurrently. *(§I-6.5.2)*

---

## Working on this codebase

33. **Read the current code before planning against it.** The design documents describe
    intent; the working tree is what exists.
34. **Every change lands with acceptance criteria** that state what "done" means in terms
    someone else can check.
35. **Record corrections, don't silently revert them.** A passage marked *Correction* or
    *Trap* is there because the obvious approach was tried and was wrong. Superseding one is
    fine; overwriting it without a note is not.
36. **A guarantee that can regress silently gets a test — over the whole codebase.** Offline
    assets, palette parity, incremental-layout equivalence, per-frame allocation, graph edge
    endpoints — anything where breakage produces no error. Two corollaries, both learned from
    tests that passed while the thing they guarded was broken: a test that scans only part of
    the tree is a convention wearing a test's clothes (rule 0.6), and where a real check is
    genuinely impossible, the rule says so and records a measurement instead (rule 25).
37. **Global usings for namespaces most files need; per-file usings for everything else.**
    `<ImplicitUsings>` already covers the BCL basics (`System`, `System.Linq`, `System.IO`,
    `System.Threading.Tasks`, …) — never re-add those explicitly. Each project's
    `GlobalUsings.cs` adds to that the small set of namespaces used across most of that
    project's files (currently `Historia.Models` in both `Historia.App` and `Historia.Core`, plus the
    core Avalonia/DI/UI namespaces in `Historia.App` — see the file for the exact list). A
    namespace only a minority of files touch — `LibGit2Sharp`, a specific `Historia.Services.*`
    area, `System.Text.Json` — stays an explicit `using` at the top of the files that need it:
    globalizing it would hide what a given file actually depends on for no real benefit. When a
    namespace crosses that "most files" threshold, move it into the project's `GlobalUsings.cs`
    and drop the now-redundant explicit `using` everywhere it appeared.
