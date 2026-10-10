---
name: historia-trace
description: Find which commit introduced a line of code and open that change in Historia at that line. Use when the user asks where a line came from, who changed it, or why it is the way it is.
allowed-tools: Bash(historia-cmdr *) Bash(git blame *) Bash(git log *) Bash(git show *)
---

# Trace a line back to its commit

1. **Find the commit.** For one line: `git blame -L <n>,<n> --porcelain -- <file>`. The first line of
   the output is `<sha> <original line> <final line>`. For a range, `git log -L <start>,<end>:<file>`.
2. **Open it in Historia** at the line as it was in that commit:
   `historia-cmdr diff <sha> <file> --line <original line>`.
   If the file was renamed since, use the path shown in the blame output (`filename`).
3. **Explain briefly:** the commit subject, author and date, and one or two sentences on why the
   change was made, from its message. Say when the message does not explain it.

## Rules

- A boundary commit (the porcelain output has a `boundary` line, or the history is shallow) is the oldest the repository
  knows about; say so rather than guessing.
- Exit code 2 means Historia is unreachable or switched off: give the commit and message in chat.
- Read-only: blame, log and show only.
