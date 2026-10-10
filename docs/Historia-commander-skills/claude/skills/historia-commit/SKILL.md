---
name: historia-commit
description: After you make one or more commits, open the first in Historia and show the historia-cmdr command for each so the user can rerun it. Use right after you commit, whenever the user asked you to commit.
allowed-tools: Bash(historia-cmdr *) Bash(git log *) Bash(git rev-parse *)
---

# Show your commits in Historia

Run this after you commit. Commit only when the user asked you to.

## Procedure

1. **Wait until you are done committing.** When one request makes several commits in a row, such as
   one per milestone, run this once after the last of them, not after each.
2. **List the commits you made**, oldest first: `git log --reverse --format='%h %s' <before>..HEAD`,
   where `<before>` is the commit `HEAD` pointed at before you started.
3. **One commit: show the command, then run it.** Print it in a fenced `bash` block so the user can
   rerun it:

   ```bash
   historia-cmdr show <sha>
   ```

   Then run it. Historia comes forward with the commit selected and centred in the graph.
4. **Several commits: open the first, list the rest.** Show and run the command for the first commit
   as above. For each of the others give the short SHA, the subject and its command in a fenced
   `bash` block, and do not run them; the user opens them when ready.

## Rules

- Exit 1: report the error from the JSON reply.
- Exit 2: Historia is unreachable or external control is switched off; say so, and point the user at
  the commands above to rerun once Historia is up.
- Navigate only: `historia-cmdr` never stages, commits or changes anything, and this skill never
  pushes.
