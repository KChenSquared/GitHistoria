#!/usr/bin/env bash
# @short: Local1 mid-bisect with detached HEAD
# @long:
#  State:
#  - Local1: mid-git-bisect with detached HEAD after 12 linear commits.
#    BISECT_LOG exists in the gitdir, operation state is bisect, and no merge/rebase
#    sequencer files exist.
#
#  Check:
#  - Conflict resolver is not opened.
#  - Operation banner displays Bisecting state without Continue / Abort buttons
#    (the Bisecting exclusion).
#  - Manual check 10.

source "$SCENARIO_LIB"
lab_begin

lab_use_template _base

lab_pin_config "$SCENARIO_LOCAL1"

# ── 1. Create 12 linear commits on main ──
mkdir -p "$SCENARIO_LOCAL1/src"
for i in $(seq 1 12); do
    echo "Commit revision index $i" >> "$SCENARIO_LOCAL1/src/history.txt"
    lab_commit "$SCENARIO_LOCAL1" "Commit step $i in linear sequence"
done

# ── 2. Start bisect, mark endpoints, and take one step ──
(
    cd "$SCENARIO_LOCAL1"
    git bisect start
    git bisect bad HEAD
    git bisect good HEAD~11
    git bisect good
)

# ── 3. Self-checks ──
lab_assert_op "$SCENARIO_LOCAL1" bisect

GDIR_L1="$(_lab_resolve_git_dir "$SCENARIO_LOCAL1")"
lab_assert "Local1 has BISECT_LOG" [ -f "$GDIR_L1/BISECT_LOG" ]
lab_assert "Local1 HEAD is detached" [ -z "$(git -C "$SCENARIO_LOCAL1" branch --show-current)" ]

lab_done
