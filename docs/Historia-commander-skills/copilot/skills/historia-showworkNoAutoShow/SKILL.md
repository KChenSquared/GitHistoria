---
name: historia-showwork
description: When you finish a task that changed files and you did not commit, show the historia-cmdr command that opens those uncommitted changes in Historia, so the user can run it. Does not automatically open Historia. Use at the end of any task that left uncommitted code changes.
---

# Show the command for your uncommitted work

Run this when the task is done, you changed files, and you did not commit.

## Procedure

1. **List the files this task changed**, from your own edits, cross-checked with
   `git status --porcelain`. Leave out changes the user already had. If none of yours remain, stop.
2. **Pick the first of them** in `git status --porcelain` order. Add `--staged` only when that file's
   change is entirely staged (a blank second status column).
3. **Show the command; do not run it.** Print it in a fenced `bash` block:

   ```bash
   historia-cmdr changes <file>
   ```

   Running it brings Historia forward on the changes panel, which lists every changed file, with that
   file's diff open.
4. **Finish with the list** of files you changed, one per line.

## Rules

- Never run `historia-cmdr`; the user decides when to open Historia.
- Never stage, unstage, commit or change anything.
