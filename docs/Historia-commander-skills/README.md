# Agent integration

`historia-cmdr` lets a coding agent point Historia at the exact commit, file diff, comparison or
uncommitted change it just produced, so you review it in a real diff viewer instead of terminal
scrollback. It can also hand you a finished change: the agent stages its own files with its own
`git add`, then asks Historia to show the staged change and pre-fill the commit message box. You
review the files and click **Commit**.

It navigates and proposes, nothing else. It never stages, commits, or changes your repository,
index, refs, working tree or settings.

This folder holds sample skills for Claude Code and GitHub Copilot (VS Code) that teach an agent
when and how to call it.

## The command

`historia-cmdr.exe` is installed beside `Historia.exe`. Put that folder on your `PATH` so agents can
run `historia-cmdr` from any repository.

| Command | What Historia shows |
|---|---|
| `historia-cmdr ping` | Nothing; checks that Historia is running and control is enabled |
| `historia-cmdr selection` | Nothing; reports the focused repository, selected commit and open diff |
| `historia-cmdr open` | The repository, opened as a tab if it is not already |
| `historia-cmdr show <rev>` | The commit, selected and centred in the graph |
| `historia-cmdr diff <rev> <file> [--line N]` | One file's diff in a commit |
| `historia-cmdr compare <base> <head> <file> [--line N]` | One file between two revisions |
| `historia-cmdr changes <file> [--staged] [--line N]` | One file's uncommitted (or staged) change |
| `historia-cmdr propose-commit (-m <text>... \| -F <file\|->)` | The staged change, with the message offered to the commit box |

Every navigation command also takes `-C <repo>` (default: the current directory), `--no-focus`
(do not bring Historia's window to the front) and `--no-launch` (do not start Historia when it is
not running). `ping` and `selection` never start Historia.

It prints one JSON object. `"ok": true` means Historia is now showing it. Exit codes:

| Code | Meaning |
|---|---|
| `0` | Done |
| `1` | The request reached Historia and failed: `repo-not-found`, `rev-not-found`, `path-invalid`, `nothing-staged`, `timeout` or `internal` |
| `2` | Historia is unreachable, or external control is switched off |
| `64` | Usage error |

## Hand a change to the user for committing

1. The agent lists the files **it** changed for this task and stages exactly those:
   `git add -- <path>...`. It never runs `git add -A`, `git add .` or `git commit`.
2. It writes the commit message in the repository's convention.
3. It pipes the message on standard input so no shell mangles quotes or newlines:

   ```bash
   historia-cmdr propose-commit -F - <<'EOF'
   feat(scope): subject line

   Body paragraph.
   EOF
   ```

   In Windows PowerShell 5.1, set the output encoding first. It otherwise pipes text to native
   programs as US-ASCII and turns `é` into `?`:

   ```powershell
   $OutputEncoding = [System.Text.UTF8Encoding]::new($false)
   @'
   feat(scope): subject line

   Body paragraph.
   '@ | historia-cmdr propose-commit -F -
   ```

4. Historia comes forward on the staging panel with the first staged file's diff open.

The reply lists `stagedPaths`, so the agent can check them against what it meant to stage, and a
`draft` field:

- `"draft": "filled"` — the commit box was empty (or held the same text) and now holds the message.
  A note under the box says a coding agent proposed it.
- `"draft": "pending"` — you already have a different draft, or the box is busy. Your draft is left
  alone and a bar above the box reads "A coding agent proposed a commit message." with **Use it** and
  **Dismiss**. **Use it** asks before replacing your draft.

`nothing-staged` (exit 1) means the index was empty; Historia's view is unchanged.

Historia never stages or commits for the agent. Files you staged before the agent ran are part of
the commit too; the staging panel shows them, and the commit skills report them rather than
unstaging them. Read the diff, not the agent's message.

## Installing the samples

Claude Code skills live in `~/.claude/skills/<name>/` (all projects) or `<repo>/.claude/skills/<name>/`
(one repository). Copy the folders from [`claude/skills/`](claude/skills):

| Skill | Use |
|---|---|
| [`historia-show`](claude/skills/historia-show/SKILL.md) | Open Historia on a commit, diff or uncommitted change |
| [`historia-review`](claude/skills/historia-review/SKILL.md) | A guided review tour of a branch, one file at a time |
| [`historia-trace`](claude/skills/historia-trace/SKILL.md) | "Where did this line come from?" |
| [`historia-commit`](claude/skills/historia-commit/SKILL.md) | Stage the agent's files and propose the commit message |

GitHub Copilot in VS Code (agent mode) reads skills from `<repo>/.github/skills/<name>/` or
`~/.copilot/skills/<name>/`, prompt files from `<repo>/.github/prompts/`, and custom instructions from
`<repo>/.github/copilot-instructions.md`. Copy the files from [`copilot/`](copilot):

| File | Install at |
|---|---|
| [`skills/historia-show/SKILL.md`](copilot/skills/historia-show/SKILL.md) | `.github/skills/historia-show/SKILL.md` |
| [`skills/historia-commit/SKILL.md`](copilot/skills/historia-commit/SKILL.md) | `.github/skills/historia-commit/SKILL.md` |
| [`prompts/historia-review.prompt.md`](copilot/prompts/historia-review.prompt.md) | `.github/prompts/historia-review.prompt.md` (run it with `/historia-review`) |
| [`copilot-instructions.snippet.md`](copilot/copilot-instructions.snippet.md) | Paste into `.github/copilot-instructions.md` |

Agent file formats change; if a sample stops loading, check the current
[Claude Code skills](https://code.claude.com/docs/en/skills) and
[VS Code agent skills](https://code.visualstudio.com/docs/copilot/customization/agent-skills)
documentation.

## Skipping the approval prompts

Agents ask before running shell commands. `historia-cmdr` only navigates and proposes, so it is
reasonable to pre-approve it.

- **Claude Code:** add the permission rule `Bash(historia-cmdr *)` to `.claude/settings.json` (or your
  user settings).
- **GitHub Copilot:** add it to the `chat.tools.terminal.autoApprove` setting:
  `"chat.tools.terminal.autoApprove": { "historia-cmdr": true }`.

Do not pre-approve `git add` or `git commit` on the agent's behalf unless you mean to.

## Security

- **Local and per-user.** Requests travel over a named pipe that only your own account can open. No
  TCP port, no URI scheme, no network call.
- **Navigate and propose only.** A commit message is offered to an editable draft you must still act
  on; it never replaces a draft you typed without asking, and nothing commits until you click
  **Commit**. A guard test fails the build if the control code gains any call that changes the
  repository, index, refs or settings.
- **Opening a repository.** Historia opens a repository it was not already showing, exactly as if you
  had opened it by hand: it joins the saved session and the Recent list, and, with auto-fetch on, it
  is fetched. Opening a repository runs git against it, which a hostile repository's configuration
  can abuse; an agent that was tricked by untrusted content could aim Historia at one. Turn the
  feature off if that worries you.
- **Input is validated.** Revisions and paths are checked before they reach a git argument list.
- **Turn it off.** Settings → Privacy & Network → *Allow command-line control (historia-cmdr)*. When it
  is off, commands fail with `"error":"disabled"` (exit 2).
- `HISTORIA_CONTROL_PIPE` overrides the pipe name; it exists for tests.
