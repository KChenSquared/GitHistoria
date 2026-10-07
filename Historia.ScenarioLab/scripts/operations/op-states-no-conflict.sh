#!/usr/bin/env bash
# @short: Local1 at rebase edit stop; Local2 in merge --no-commit
# @long:
#  State:
#  - Local1: stopped cleanly at an interactive-rebase 'edit' stop (no conflicts).
#    Operation state is rebase, ls-files -u is empty.
#  - Local2: in a clean 'git merge --no-commit --no-ff' state with staged changes
#    ready to commit, but no conflicts. Operation state is merge, ls-files -u is empty.
#
#  Check:
#  - "Not shown" rule: graph and staging views remain visible, no conflict resolver
#    is opened, operation banner displays Continue / Abort actions.
#  - Manual check 10.

source "$SCENARIO_LIB"
lab_begin

lab_use_template _base

lab_pin_config "$SCENARIO_LOCAL1"
lab_pin_config "$SCENARIO_LOCAL2"

# ── 1. Local1: Setup interactive rebase stopped at clean 'edit' ──
mkdir -p "$SCENARIO_LOCAL1/features"
echo "Feature 1 implementation" > "$SCENARIO_LOCAL1/features/f1.txt"
lab_commit "$SCENARIO_LOCAL1" "Add feature 1"

echo "Feature 2 implementation" > "$SCENARIO_LOCAL1/features/f2.txt"
lab_commit "$SCENARIO_LOCAL1" "Add feature 2"

echo "Feature 3 implementation" > "$SCENARIO_LOCAL1/features/f3.txt"
lab_commit "$SCENARIO_LOCAL1" "Add feature 3"

# Rebase last 3 commits with commit 2 set to 'edit'
(
    cd "$SCENARIO_LOCAL1"
    GIT_SEQUENCE_EDITOR="sed -i -e '2s/^pick/edit/'" git rebase -i HEAD~3
)

# ── 2. Local2: Setup clean merge --no-commit ──
mkdir -p "$SCENARIO_LOCAL2/src"
echo "Initial baseline service" > "$SCENARIO_LOCAL2/src/base.txt"
lab_commit "$SCENARIO_LOCAL2" "Initial baseline service"
git -C "$SCENARIO_LOCAL2" push origin main

# Branch feature/clean-module with an independent non-overlapping file
git -C "$SCENARIO_LOCAL2" checkout -b feature/clean-module main
echo "Independent module content" > "$SCENARIO_LOCAL2/src/module.txt"
lab_commit "$SCENARIO_LOCAL2" "Add independent module"
git -C "$SCENARIO_LOCAL2" push -u origin feature/clean-module

# Switch back to main and run merge --no-commit --no-ff
git -C "$SCENARIO_LOCAL2" checkout main
git -C "$SCENARIO_LOCAL2" merge --no-commit --no-ff origin/feature/clean-module

# ── 3. Self-checks ──
# Local1: op is rebase, no conflicts
lab_assert_op "$SCENARIO_LOCAL1" rebase
L1_UNMERGED="$(git -C "$SCENARIO_LOCAL1" ls-files -u)"
lab_assert "Local1 has no unmerged entries" [ -z "$L1_UNMERGED" ]

# Local2: op is merge, no conflicts, staged changes exist
lab_assert_op "$SCENARIO_LOCAL2" merge
L2_UNMERGED="$(git -C "$SCENARIO_LOCAL2" ls-files -u)"
lab_assert "Local2 has no unmerged entries" [ -z "$L2_UNMERGED" ]
lab_assert "Local2 has staged changes" [ -n "$(git -C "$SCENARIO_LOCAL2" diff --cached --name-only)" ]

lab_done
