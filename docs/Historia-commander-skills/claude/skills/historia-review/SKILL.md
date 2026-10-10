---
name: historia-review
description: Walk the user through a branch or change set one file at a time in Historia, with a short note on what to look at in each stop. Use when the user asks for a guided review, a review tour, or to "walk me through" a diff or branch.
allowed-tools: Bash(historia-cmdr *) Bash(git diff *) Bash(git log *) Bash(git merge-base *) Bash(git rev-parse *)
---

# Guided review in Historia

Take the user through a change set as a tour. Historia shows each file's diff; you say what to look at.

## Procedure

1. **Pick the base.** Use the base the user names; otherwise the branch's upstream, otherwise `main`.
2. **List the changes:** `git diff --name-status <base>...HEAD`. Find the fork point with
   `git merge-base <base> HEAD` and compare against it, so commits that landed on the base after the
   branch started do not show up as reversed changes.
3. **Order the files for reading:** interfaces and models first, then implementations, then call
   sites, then tests, then configuration and docs. Skip generated files and pure renames unless the
   user asks.
4. **Present the plan:** the ordered list, one line each saying why the file is where it is.
5. **Show stop 1:** run
   `historia-cmdr compare <merge-base> HEAD <file> --line <first changed line>`
   and add a 2–4 line note: what changed, what to check, anything risky.
6. **Wait for the user** before each next stop. They say "next", "back", "skip to N", or "done".
   Only then show the next file the same way.
7. **Collect their comments** as they make them. At the end, summarize them as a list of follow-ups.

## Rules

- Never edit files during the tour unless the user asks.
- Exit code 2 means Historia is unreachable or switched off: say so once and offer to continue in chat
  with `git diff` output instead.
- Use `--no-focus` after the first stop so Historia does not steal focus from the conversation.
- The notes are your reading of the diff; tell the user to trust the diff in Historia over your summary.
