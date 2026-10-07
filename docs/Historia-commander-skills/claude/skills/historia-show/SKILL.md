---
name: historia-show
description: Open Historia (the user's Git GUI) on a specific commit, file diff, comparison or uncommitted change so the user can review it visually. Use after making a commit, when the user asks to see or review a change "in Historia", or when pointing at where a line came from.
allowed-tools: Bash(historia-cmdr *)
---

# Show it in Historia

Run `historia-cmdr` from the repository directory (or pass `-C <repo>`). It prints one JSON object;
`"ok": true` means Historia is now showing it.

| You want to show | Command |
|---|---|
| a commit | `historia-cmdr show <rev>` |
| one file in a commit | `historia-cmdr diff <rev> <file> [--line N]` |
| a file between two revisions | `historia-cmdr compare <base> <head> <file> [--line N]` |
| an uncommitted change | `historia-cmdr changes <file> [--staged] [--line N]` |
| what the user is looking at | `historia-cmdr selection` |
| staged changes, ready for the user to commit | use the `historia-commit` skill |

`--line N` is a line of the new (right-hand) file; Historia scrolls to it.

## Procedure

1. Pick the command from the table. After you commit, `show HEAD` for the commit or `diff HEAD <file>`
   for the file that matters most. For work that is not committed, `changes <file>`.
2. Run it and read the JSON.
3. Report one line, for example: "Opened `a1b2c3d` in Historia."

## Rules

- Only these commands exist, and none of them changes the repository.
- Pass revisions and paths exactly as git reports them.
- Exit code 2 means Historia is unreachable or external control is switched off. Tell the user once
  and carry on without it. Never retry in a loop.
- Exit code 1 carries an `error` (`rev-not-found`, `path-invalid`, …); fix the argument or say what
  went wrong.
- Add `--no-focus` when the user is typing elsewhere and should not be interrupted.
- Do not call it repeatedly for the same change.
