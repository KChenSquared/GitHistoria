# Contributing to Historia

Thank you for your interest in contributing to **Historia**! We are building a blazing-fast, open-source Git client for Windows, and we welcome contributions of all kinds: bug fixes, performance optimizations, new features, documentation improvements, and UI refinements.

---

## 1. Core Principles & Philosophy

When contributing to Historia, keep these core tenets in mind:

0. **Confirm Before Destroying Anything (highest priority):** No code path may discard the
   user's uncommitted work or irreversibly rewrite their history without an explicit
   confirmation the user actively accepts. The confirmation is a modal alert
   (`IDialogService.ConfirmAsync` / `ChooseAsync`) with the red `triangle-alert`
   (`DialogIcon.Warning`), it names the exact effect, and **Cancel** is the primary (blue)
   button and the keyboard default (`defaultToCancel: true`) while remaining the rightmost
   button. See constitution **Rule 00**. This outranks every other principle below, including
   performance.
1. **Performance First:** Historia is built to match the speed of native clients like Git-Fork. Avoid unnecessary allocations in hot paths (commit log parsing, DAG calculation, diff rendering). Always profile and benchmark performance-critical code.
2. **Never Block the UI Thread:** All Git operations, I/O, and heavy computations must execute on background threads and communicate via non-blocking asynchronous patterns.
3. **Simplicity & Polish:** Strive for clean, intuitive UI interactions. Complex Git concepts should be rendered clearly and visually.
4. **Progressive Disclosure:** Design interfaces that reveal complexity only as needed. Common workflows (staging, unstaging, committing, switching branches, reading the graph) must remain streamlined, clutter-free, and instantly approachable. Advanced capabilities (interactive rebase, worktree management, submodules, terminal access, deep diagnostic metadata) should be surfaced gracefully through context menus, collapsible drawers, or dedicated drill-downs rather than dominating the primary screen space. Errors must show clean, actionable human descriptions first, preserving raw Git stderr behind a details disclosure.

---

## 2. Prerequisites & Environment Setup

### Required Tools
- **Operating System:** Windows 10 (version 1809 / build 17763 or higher) or Windows 11
- **.NET SDK:** .NET 10.0 SDK (or latest installed version)
- **IDE:**
  - [Visual Studio 2022 / 2025](https://visualstudio.microsoft.com/) with the **.NET Desktop Development** workload, OR
  - [Visual Studio Code](https://code.visualstudio.com/) with the **C# Dev Kit** extension, OR
  - [JetBrains Rider](https://www.jetbrains.com/rider/) with .NET desktop support.

---

## 3. Building and Running Locally

### Clone the Repository
```bash
git clone https://github.com/KChenSquared/Historia.git
cd Historia
```

### Restore Dependencies & Build
```powershell
dotnet restore
dotnet build
```

### Run the Application
```powershell
dotnet run
```

---

## 4. Codebase Organization

```
Historia/
├── Historia.slnx              # Main solution file
├── Historia.App/              # Avalonia UI application project
├── Historia.Core/             # Portable domain models, services, and git gateway
├── tests/Historia.Tests/      # Automated test suite
├── assets/                    # Application logos, splash art, and icons
├── dev/                       # Automation scripts and development tools
└── docs/                      # Project documentation and guidelines
```

---

## 5. Coding Standards & Best Practices

### C# Conventions
- Target modern C# (file-scoped namespaces, nullable reference types enabled, target-typed `new()`, pattern matching).
- Use `readonly record struct` or `record class` for immutable data transfer objects.
- Prefer `ValueTask` / `Task` for all async operations and pass `CancellationToken` throughout long-running operations.
- In hot paths (graph calculation, diff parsing), use `Span<T>`, `ReadOnlySpan<T>`, and `ArrayPool<T>` where appropriate to avoid heap allocations.

### Avalonia UI Conventions
- Views are hand-built in C# in the `ViewKit` style (`Historia.App/Views/`), matching the native-control layer; XAML is reserved for `App.axaml` and `MainWindow.axaml`.
- Never hard-code colors. `HistoriaPalette` (`Historia.Core/Services/Theming/`) is the single source of truth; bind through the generated `{DynamicResource ...}` keys so light/dark theming keeps working.
- Always use virtualization when rendering large collections such as commit lists or long diffs (see `CommitGraphControl`, which renders only the visible row range).

---

## 6. Git & Contribution Workflow

### 1. Branch Naming
Create a feature branch from `main` using the following prefixes:
- `feat/feature-name` — New features
- `fix/bug-description` — Bug fixes
- `perf/optimization-target` — Performance improvements
- `docs/topic-name` — Documentation changes
- `refactor/scope` — Code refactoring without behavioral changes

### 2. Commit Messages
We adhere to [Conventional Commits](https://www.conventionalcommits.org/):
```
<type>(<scope>): <short summary>

[optional body]

[optional footer(s)]
```
*Examples:*
- `feat(graph): implement dynamic lane allocation for merge commits`
- `fix(staging): resolve hunk stage offset calculation bug`
- `perf(diff): cache lexer tokens during side-by-side render`

### 3. Pull Request Guidelines
1. Ensure your changes build cleanly without warnings (`dotnet build`).
2. Run any existing unit and integration tests (`dotnet test`).
3. For performance-sensitive code, include benchmark results or profiling notes in the PR description.
4. Keep PRs focused on a single logical change for ease of review.

---

## 7. Reporting Issues & Requesting Features

- **Bug Reports:** Open an issue with clear reproduction steps, repo size/context, your Windows & .NET version, and any relevant error logs or screenshots.
- **Feature Proposals:** Start a discussion or open an issue outlining the user problem, proposed UX flow, and technical considerations.

---

## 8. License

By contributing to Historia, you agree that your contributions will be licensed under the project's existing open-source license, the [GNU General Public License v3.0](../LICENSE).

Bundled third-party material is tracked in [`docs/third-party-licenses.md`](third-party-licenses.md).
The utility icon set is [Lucide](https://lucide.dev/) (ISC), vendored under `assets/lucide/`
and regenerated with `dev/vendor-lucide.ps1` — add new glyphs from the pinned Lucide
release rather than hand-drawing them.
