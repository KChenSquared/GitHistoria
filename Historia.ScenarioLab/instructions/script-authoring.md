# ScenarioLab — Script Authoring Instructions

This guide explains how to write and run bash scenario scripts for `Historia.ScenarioLab`.

---

## 1. What ScenarioLab Is

`Historia.ScenarioLab` is a standalone developer tool for creating, managing, and running automated bash scripts that reproduce real git scenarios (merge conflicts, submodule configurations, complex rebase states, etc.) across scratch directories. Historia can then be pointed at these scratch repositories to exercise UI and core git workflows in reproducible environments.

---

## 2. Folders and Environment Variables

Every scenario script runs under Git Bash (`bash.exe --noprofile --norc`) inside an isolated, temporary working directory. The runner exposes the following environment variables (using forward slashes):

| Variable | Description |
|---|---|
| `SCENARIO_LOCAL1` | Path to the Local 1 scratch working tree directory. |
| `SCENARIO_LOCAL2` | Path to the Local 2 scratch working tree directory. |
| `SCENARIO_REMOTES` | Path to the Remotes directory containing bare repositories. |
| `SCENARIO_TEMPLATES` | Path to the tracked `templates/` directory in `Historia.ScenarioLab`. |
| `SCENARIO_LIB` | Path to the bash helper library (`lib/scenario.sh`). |
| `SCENARIO_ROOT` | Path to the `Historia.ScenarioLab` project root directory. |
| `SCENARIO_NAME` | Name of the script without the `.sh` extension (e.g. `merge-conflict-3-files`). |
| `SCENARIO_WORK` | Automatically created temporary directory for intermediate clones; automatically removed on exit. |

Git environment variables are also pre-configured by the runner:
- `GIT_TERMINAL_PROMPT=0` (disables interactive credential prompts)
- `GIT_EDITOR=true` (disables interactive editors)
- `GIT_SEQUENCE_EDITOR=true` (disables interactive rebase editors)
- `GIT_PAGER=cat` (disables pagination)
- `PAGER=cat`

Standard input is closed; scripts must never require interactive user input.

---

## 3. Script Header Grammar

Every scenario script begins with a structured comment header parsed by the ScenarioLab UI:

```bash
#!/usr/bin/env bash
# @short: <description in 72 characters or fewer>
```

Optional long description:
```bash
#!/usr/bin/env bash
# @short: Local1 must merge Local2's pushed commit: conflicts in 3 files
# @long:
#  This scenario reproduces a multi-file merge conflict between Local1 and Local2.
#
#  Local1 has fetched origin/main but has not yet merged it.
```

Rules:
1. **Line 1** must be `#!/usr/bin/env bash`.
2. `# @short:` is **required** and must not exceed 72 characters.
3. `# @long:` is **optional**. Each line of the body must start with `#  ` (hash followed by two spaces), and paragraph breaks are represented by a lone `#`.
4. The header block terminates at the first line that does not match this structure. Any ordinary comments or script commands follow after.

---

## 4. The Body Contract

After the header, every script must follow this contract:

```bash
source "$SCENARIO_LIB"
lab_begin

# ... scenario setup ...

lab_done
```

Rules:
- `lab_begin` enables `set -euo pipefail`, verifies all environment variables, checks that `Local1`, `Local2`, and `Remotes` are completely empty, exports the deterministic git configuration, and starts the clock.
- **Never touch paths outside the `SCENARIO_*` variables.**
- **Never run interactive commands.**

---

## 5. Remotes and Submodules Layout

- `SCENARIO_REMOTES/mainRemote` is the central bare repository that locals clone, fetch from, and push to.
- Additional bare repositories (such as submodules) live alongside it as `SCENARIO_REMOTES/<subname>`.
- In submodules, add remotes using **relative URLs** (e.g. `../<subname>`), resolving relative to `mainRemote`.
- **Note on local file cloning (CVE-2022-39253):** Git blocks `file://` transport submodules by default. The lab git configuration explicitly sets `protocol.file.allow=always`. When cloning with submodules manually in Git or Historia, pass `-c protocol.file.allow=always` if necessary.

---

## 6. Templates and the Cache Pattern

Templates live in `templates/<name>/` and contain snapshot directories:
- `remote-git` — bare repository copied to `$SCENARIO_REMOTES/mainRemote`
- `<sub>-git` — bare repository copied to `$SCENARIO_REMOTES/<sub>`
- `local1-git` — copied to `$SCENARIO_LOCAL1/.git`
- `local2-git` — copied to `$SCENARIO_LOCAL2/.git`

### Shipped `_base` Template
The shared `_base` template (`templates/_base`) contains:
- `remote-git`: a bare repository with an initial commit adding `README.md` on `main`.
- `local1-git` & `local2-git`: clones tracking `origin/main`.

Load it at the beginning of a scenario:
```bash
lab_use_template _base
```

### Script-Specific Templates and Caching
A script may snapshot its own state into `templates/<SCENARIO_NAME>/` via `lab_save_template`:

```bash
if lab_has_template; then
    lab_use_template
else
    # Build scenario from scratch...
    lab_save_template
fi
```

**Staleness Caveat:** When using the cache pattern, subsequent runs reuse the cached snapshot. If you modify the script generation logic, delete the `templates/<script-name>/` directory so ScenarioLab rebuilds it.

---

## 7. Deterministic Commits and Time

To ensure identical commit SHAs across repeated runs, `scenario.sh` enforces:
- Fixed identity: `ScenarioLab <scenariolab@example.com>`.
- Deterministic base date: `2026-01-05T09:00:00Z`.
- `lab_tick [seconds]` advances the clock (default: +3600 seconds).
- `lab_commit <dir> <message>` automatically ticks the clock, stages all changes (`git add -A`), and creates a commit.

---

## 8. Naming Rules

- Folder and script names must match regex `^[A-Za-z0-9._-]+$`.
- Names cannot start with an underscore (`_` is reserved for shared templates like `_base`).
- Script names must be unique across the entire `scripts/` tree.

---

## 9. Self-Checks with `lab_assert`

Use `lab_assert <description> <command...>` to verify that the scenario created the exact intended git state:

```bash
lab_assert "Local1 is 1 ahead and 1 behind origin" [ "$AHEAD_BEHIND" = "1	1" ]
lab_assert "mainRemote has release tag v1.0.0" git -C "$SCENARIO_REMOTES/ui-kit" rev-parse --verify refs/tags/v1.0.0
```

If a command fails, `lab_assert` outputs `[FAIL] <description>` and halts execution immediately.

---

## 10. Shipped Scenario Examples

1. **`scripts/merge/merge-conflict-3-files.sh`**:
   Loads `_base`, creates conflicting edits in `config/app.ini`, `src/greeting.py`, and `docs/guide.md` between Local 1 and Local 2, pushes Local 2's changes, commits Local 1's changes, and fetches without merging. Confirms 3 / 2 / 3 conflicting hunks via `git merge-tree`.

2. **`scripts/clone/clone-remote-4-submodules.sh`**:
   Constructs 4 bare submodule repositories (`core-lib`, `ui-kit`, `net-utils`, `theme-assets`) with multiple branches, tags, and commits, and links them via relative URLs into `mainRemote`. Verifies recursive submodule cloning and branch pointers.

---

## 11. Minimal Script Skeleton

```bash
#!/usr/bin/env bash
# @short: <describe the scenario in under 72 characters>

source "$SCENARIO_LIB"
lab_begin

lab_done
```
