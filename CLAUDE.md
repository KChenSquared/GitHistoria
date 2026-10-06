# CLAUDE.md

This file exists so Claude Code loads the shared instructions in `AGENTS.md`.

@AGENTS.md

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
