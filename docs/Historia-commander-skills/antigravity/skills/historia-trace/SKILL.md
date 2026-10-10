---
name: historia-trace
description: Find which commit introduced a line of code and open that change in Historia at that line. Use when the user asks where a line came from, who changed it, or why it is the way it is.
---

# Trace a line back to its commit

1. **Find the commit.** For one line: `git blame -L <n>,<n> --porcelain -- <file>`. The first line of
   the output is `<sha> <original line> <final line>`. For a range:
   `git blame -L <start>,<end> --porcelain -- <file>`, and collect the distinct commits.
   If the file was renamed since, use the path shown in the blame output (`filename`).
2. **Build the command** for each commit, at the line as it was in that commit:
   `historia-cmdr diff <sha> <file> --line <original line>`.
3. **One commit: show the command and run it.** Print the command in a fenced `bash` block so the user
   can rerun it, then run it.
4. **More than one commit: list them and open none.** For each commit give the short SHA, subject,
   author, date, the lines it owns, and its command in a fenced `bash` block. Do not run any of them;
   the user picks which to open.
5. **Explain briefly:** the commit subject, author and date, and one or two sentences on why the
   change was made, from its message. Say when the message does not explain it.

## Rules

- A boundary commit (the porcelain output has a `boundary` line, or the history is shallow) is the oldest the repository
  knows about; say so rather than guessing.
- Exit code 2 means Historia is unreachable or switched off: give the commit and message in chat, and
  keep the command shown so the user can run it once Historia is up.
- Read-only: blame, log and show only.
