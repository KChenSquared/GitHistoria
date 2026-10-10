---
name: historia-showwork
description: When you finish a task that changed files and you did not commit, open Historia on the uncommitted changes with the first changed file's diff open, and show the historia-cmdr command so the user can rerun it. Use at the end of any task that left uncommitted code changes.
---

# Show your uncommitted work in Historia

Run this when the task is done, you changed files, and you did not commit.

## Procedure

1. **List the files this task changed**, from your own edits, cross-checked with
   `git status --porcelain`. Leave out changes the user already had. If none of yours remain, stop.
2. **Pick the first of them** in `git status --porcelain` order. Add `--staged` only when that file's
   change is entirely staged (a blank second status column).
3. **Show the command, then run it.** Print it in a fenced `bash` block so the user can rerun it:

   ```bash
   historia-cmdr changes <file>
   ```

   Then run it. Historia comes forward on the changes panel, which lists every changed file, with that
   file's diff open.
4. **Finish with the list** of files you changed, one per line, and say the rest are in Historia's
   changes list.

## Rules

- Exit 1: report the error from the JSON reply and try the next file.
- Exit 2: Historia is unreachable or external control is switched off; say so, and point the user at
  the command above to rerun once Historia is up.
- Navigate only: never stage, unstage, commit or change anything.
