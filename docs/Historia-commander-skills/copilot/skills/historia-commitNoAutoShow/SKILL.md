---
name: historia-commit
description: After you make one or more commits, show the historia-cmdr command that opens each in Historia, so the user can run it. Does not automatically open Historia. Use right after you commit, whenever the user asked you to commit.
---

# Show the commands for your commits

Run this after you commit. Commit only when the user asked you to.

## Procedure

1. **Wait until you are done committing.** When one request makes several commits in a row, such as
   one per milestone, run this once after the last of them, not after each.
2. **List the commits you made**, oldest first: `git log --reverse --format='%h %s' <before>..HEAD`,
   where `<before>` is the commit `HEAD` pointed at before you started. If you did not note it,
   list the SHAs your `git commit` calls printed instead.
3. **Show the commands; do not run them.** For each commit give the short SHA, the subject and its
   command in a fenced `bash` block:

   ```bash
   historia-cmdr show <sha>
   ```

   Running it brings Historia forward with the commit selected and centred in the graph.

## Rules

- Never run `historia-cmdr`; the user decides when to open Historia.
- This skill never pushes.
