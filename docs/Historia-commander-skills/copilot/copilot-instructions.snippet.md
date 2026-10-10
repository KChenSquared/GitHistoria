## Reviewing your changes in Historia

Historia is the user's Git GUI, and `historia-cmdr` points it at a change. When you finish a task that
left uncommitted changes, use the `historia-showwork` skill; after you commit, use the
`historia-commit` skill. When the user wants to commit themselves,
stage only the files you changed and propose the message with the `historia-propose-commit` skill
(`historia-cmdr propose-commit -F -`). `historia-cmdr` only navigates and proposes: it never stages or
commits, and you must not run `git add -A`, `git add .` or `git commit` for this. Do not run it
repeatedly for the same change; if it exits with code 2, Historia is not available, so carry on
without it.
