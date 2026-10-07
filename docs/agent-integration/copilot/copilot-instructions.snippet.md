## Reviewing your changes in Historia

After you finish an edit or make a commit, offer to open it in Historia, the Git GUI, with
`historia-cmdr changes <file>` (uncommitted) or `historia-cmdr show HEAD` (committed). If the user
prefers to commit themselves, stage only the files you changed and propose the message with the
`historia-commit` skill (`historia-cmdr propose-commit -F -`). `historia-cmdr` only navigates and
proposes: it never stages or commits, and you must not run `git add -A`, `git add .` or `git commit`
for this. Do not run it repeatedly for the same change; if it exits with code 2, Historia is not
available, so carry on without it.
