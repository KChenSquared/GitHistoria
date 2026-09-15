# CLAUDE.md

Guidance for Claude Code when working in this repository (Historia).

## Read these first

- [Historia Constitution](docs/constitution.md) — architectural invariants, security policies,
  and performance ceilings. Rule 00 (Confirm Before Destroying Anything) outranks every other
  principle in this codebase, including performance.
- [Comprehensive Design Documentation](Historia-DevDocs/ComprehensiveDesign/README.md) — technical
  architecture, Git data access strategy, and the DAG graph layout engine. This is the living
  spec; consult it before making architectural changes.
- [Version Plans](Historia-DevDocs/versionPlans/) — the active milestone's plan. Older plans are
  archived to keep the working set small.
- [Contributing Guide](docs/contributing.md) — environment setup, coding standards, PR
  guidelines, and core tenets.

## Subagents

Do not use the Agent tool to spawn subagents (Explore, general-purpose, etc.) by default. Run
all work — search, research, multi-step tasks — directly in the main session.

**Why:** User wants to save credits. Spawning subagents costs extra and duplicates context
re-derivation.

**How to apply:** Only spawn a subagent when the user explicitly asks for one (e.g. "use a
subagent for this", "spawn an agent to..."). Otherwise default to doing the work yourself with
direct tools (Read, Grep, Glob, Bash, etc.), even for broad or multi-step exploration. This
doesn't restrict running tools/commands in the background (e.g. `run_in_background` on Bash,
long-running builds/tests, background Monitor usage) — that's still fine and encouraged when
appropriate; it's only subagent spawning that requires explicit ask.
