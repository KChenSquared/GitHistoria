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

Structured long description with state and checklist:
```bash
#!/usr/bin/env bash
# @short: Local1 mid-merge (merge style); Local2 the same merge under zdiff3
# @long:
#  State:
#  - Local1: mid-merge on feature/login-rework (conflictStyle=merge).
#  - Local2: mid-merge on feature/login-rework (conflictStyle=zdiff3).
#
#  Check:
#  - Conflict resolver UI navigation, inline spans, and region resolution.
```

Rules:
1. **Line 1** must be `#!/usr/bin/env bash`.
2. `# @short:` is **required** and must not exceed 72 characters.
3. `# @long:` is **optional**. Each line of the body must start with `#  ` (hash followed by two spaces), and paragraph breaks are represented by a lone `#`.
4. Header `@long` blocks should clearly specify `State:` (what Local1 and Local2 contain when opened) and `Check:` (what items to verify in Historia).
5. The header block terminates at the first line that does not match this structure. Any ordinary comments or script commands follow after.

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
- **Never run interactive commands.** Standard input is redirected from `/dev/null`.
- **Pinned configuration:** Always pin repository configuration using `lab_pin_config <dir>` on every local left for Historia. Historia reads the user's system and global configuration, so repository-level settings ensure reproducibility across machines.
- **Mid-operation scripts:** Scenarios leaving a repository mid-operation (merge, rebase, cherry-pick, revert, etc.) must start from `lab_use_template _base` and cannot use custom templates.
- **Do not commit after conflicts:** Never invoke `lab_commit` after triggering an expected conflict, as `lab_commit` executes `git add -A` which would mark all conflicts resolved.
- **Byte properties and line endings:** Script files must strictly use LF line endings and contain no CR, BOM, or NUL bytes. Special file contents (BOM, CRLF, NUL, Latin-1, UTF-16) must be generated dynamically using `printf` escapes or `iconv`.

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

### Template Limitations for Mid-Operation States
Per-script templates cannot snapshot working trees left in conflict (since `lab_save_template` archives `.git` only, and `lab_use_template` runs `git reset --hard`). Mid-operation scripts build directly from `_base` in a few seconds. Under headless execution (`SCENARIO_HEADLESS=1`), `lab_save_template` is safely treated as a no-op so tracked template directories are never modified during test runs.

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

## 9. Helper Library Functions

`lib/scenario.sh` provides helpers for building, controlling, and asserting scenario state:

### Repository Configuration & Requirements
- **`lab_pin_config <dir> [key value]...`**: Pins `core.autocrlf false` and `merge.conflictStyle merge` in `<dir>`'s local config, followed by any additional key-value overrides.
- **`lab_require <command> <reason>`**: Checks if `<command>` is runnable on the current system. If not, prints `[SKIP] <reason>` and exits with code 3.

### Operation & Conflict Execution
- **`lab_expect_conflict <dir> <desc> <git-command...>`**: Runs the specified git command in `<dir>` and verifies that `git ls-files -u` is non-empty, regardless of the process exit code (`checkout -m` exits 0 on conflict, whereas merge/rebase/pick exit 1). Fails if no unmerged files exist.
- **`lab_gen_lines <file> <count> <prefix>`**: Deterministically generates `<count>` lines using `awk` with format `"<prefix> %06d <fixed filler>"` without randomness.

### Assertions & State Checks
- **`lab_assert <desc> <command...>`**: Evaluates `<command...>`; prints `[FAIL] <desc>` and exits 1 if the command returns non-zero.
- **`lab_assert_status <dir> <path> <XY>`**: Verifies that `git status --porcelain=v1 -- <path>` starts with `${XY} ` (e.g. `UU`, `AA`, `UD`, `DU`, `AU`, `UA`, `DD`, `M `, ` M`, `MM`, `R `).
- **`lab_count_markers <file> <char> [size] [encoding]`**: Returns the number of conflict marker lines starting with exactly `<size>` repeats (default 7) of `<char>` (`<`, `|`, `=`, `>`) followed by a space, `\r`, or end-of-line. When `<encoding>` is supplied, decodes through `iconv -f <enc> -t UTF-8` first.
- **`lab_assert_op <dir> <none|merge|rebase|cherry-pick|revert|bisect>`**: Inspects git sequencer markers (`rebase-merge/`, `rebase-apply/`, `CHERRY_PICK_HEAD`, `REVERT_HEAD`, `MERGE_HEAD`, `BISECT_LOG`) and asserts that the operation matches the expected state.
- **`lab_done`**: Prints commit logs for Local 1, Local 2, and bare remotes, reports detected operation state and porcelain status for each local repository, and prints completion success banner.

---

## 9.1. Headless Runner and Linter

`lib/run-headless.sh` allows running and validating scenarios from the command line without opening the ScenarioLab GUI:

```bash
bash Historia.ScenarioLab/lib/run-headless.sh [--twice] [--keep] [--lint-only] [path...]
```

Options:
- `path...`: One or more script files or directories (default: `scripts/`).
- `--lint-only`: Checks script formatting, headers, uniqueness, line endings, and safety rules without executing.
- `--twice`: Runs each scenario twice in isolated sandboxes and compares cryptographic fingerprints (refs, HEAD, index stages, porcelain status, sequencer files, and file SHA1s) to ensure strict determinism across runs.
- `--keep`: Preserves the temporary sandbox directories instead of removing them on exit.

Exit codes:
- `0`: All scripts PASSED or SKIPPED cleanly.
- `1`: One or more scripts FAILED or lint errors occurred.
- `3`: (Internal per-script exit code for SKIPPED requirements).

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
