---
name: historia-propose-commit
description: Stage only the files this task changed and hand them to the user in Historia with a proposed commit message, so the user reviews the staged files and clicks Commit themselves. Does not automatically open Historia. Use when the user says "stage it for me", "let me commit it" or "hand it to Historia", or when you finish a task and the user prefers to commit themselves.
---

# Hand the change to the user for committing

You stage; the user commits. Historia never stages or commits for you.

## Procedure

1. **List the files this task changed**, from your own edits, cross-checked with
   `git status --porcelain`. Never include changes the user already had.
2. **Stage exactly those files:** `git add -- <path>...` with explicit paths.
   - Never run `git add -A`, `git add .` or `git commit`.
   - Never run `git reset` or anything else that writes refs or rewrites the index beyond those paths.
   - If files were already staged before the task, say so; do not unstage them.
3. **Write the message in the repository's convention.** Read `git log --oneline -10` first. Subject
   at most 72 characters, body wrapped, no trailers unless the repository uses them.
4. **Pipe it on standard input** so no shell mangles quotes or newlines. Never put a long message on
   the command line with `-m`.

   bash, with a quoted heredoc:

   ```bash
   historia-cmdr propose-commit -F - <<'EOF'
   feat(scope): subject line

   Body paragraph.
   EOF
   ```

   PowerShell: set the encoding first. Windows PowerShell 5.1 pipes to native programs as US-ASCII and
   turns `é` into `?`, and `[Text.Encoding]::UTF8` adds a byte-order mark.

   ```powershell
   $OutputEncoding = [System.Text.UTF8Encoding]::new($false)
   @'
   feat(scope): subject line

   Body paragraph.
   '@ | historia-cmdr propose-commit -F -
   ```

5. **Read the JSON reply.**
   - Compare `stagedPaths` with what you meant to stage and report any difference.
   - `"draft": "pending"`: tell the user their existing draft was kept and Historia is offering the
     message in a bar above the box.
   - `nothing-staged` (exit 1): say so and stop.
   - Exit 2: Historia is unreachable or external control is switched off; show the message in chat instead.
6. **Finish with one line:** the files are staged and the message is in Historia's commit box; review
   and click Commit.

## Rules

- Never commit on the user's behalf in this flow.
- Tell the user to read the staged diff, not your summary of it.
