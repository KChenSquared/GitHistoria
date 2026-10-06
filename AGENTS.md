# AGENTS.md

Instructions for coding agents working in this repository (Historia, a Git client built with C# and
Avalonia). This file is the single source; `CLAUDE.md` and `.github/copilot-instructions.md` point here.

## Read first

- [Historia Constitution](docs/constitution.md) — the invariants. Rule 00 (confirm before destroying
  anything) outranks every other rule, including performance. If a rule blocks you, stop and report;
  do not route around it.
- [Comprehensive Design Documentation](Historia-DevDocs/ComprehensiveDesign/README.md) — the living
  spec. Read the relevant section before changing architecture.
- Active plans: [`Historia-DevDocs/AdHocPlans/`](Historia-DevDocs/AdHocPlans/). Finished plans move
  to `completed/`.
- [Contributing Guide](docs/contributing.md) — setup, coding standards, PR guidelines.

## Build and test

```powershell
dotnet build Historia.App                     # the app; required after App changes
dotnet test tests/Historia.Tests              # Core tests; works while Historia is running
dotnet test tests/Historia.App.Tests          # headless UI tests; close Historia if the build cannot copy its exe
.\dev\test.ps1 -Tier fast                     # also: git, ui, slow, all
```

## Rules agents trip over most

One line each; the constitution has the full text.

- Destructive actions confirm through `IDialogService.ConfirmDestructiveAsync` (Rule 00).
- Offline first: no network URL in any `.cs` or `.axaml`, no unrequested network call (Rule 0).
- Git access is the CLI only, with arguments in `ArgumentList`, `--` before paths (Rules 6 and 9).
- Never block the UI thread; mutate off it and raise events on it (Rules 18 and 22).
- Global usings only: every `using` directive lives in the project's `GlobalUsings.cs` (Rule 37).
- Comments describe the present: no transition comments and no references to documents or rule
  numbers (Rule 39).
- Agent-facing surfaces navigate and propose only; they never stage, commit or change anything
  (Rule 40).

## Plans

Work that touches several files follows the phased plan format: sequential phases, parallel
milestones that own disjoint files, automated gates, and design docs updated last. See
[Working with coding agents](docs/contributing.md#6-working-with-coding-agents).

## Commits

Conventional Commits, subject under 72 characters, one commit per milestone. Never push; the
maintainer pushes.

## Reviewing your work

After a change, offer to open it in Historia: `historia-cmdr show HEAD` for a commit, or
`historia-cmdr changes <file>` for an uncommitted change. When the maintainer wants to commit it
themselves, stage only the files you changed with explicit paths and run
`historia-cmdr propose-commit -F -` with the message on standard input instead of committing. See
[`docs/agent-integration/`](docs/agent-integration/README.md).
