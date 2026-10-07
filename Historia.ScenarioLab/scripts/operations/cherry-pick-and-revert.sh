#!/usr/bin/env bash
# @short: Local1 mid-cherry-pick of a range; Local2 mid-revert
# @long:
#  State:
#  - Local1: mid-cherry-pick of hotfix/1.4 range, stopped on 2nd commit with conflict,
#    leaving the 3rd commit pending in sequencer/todo.
#  - Local2: mid-revert of an earlier commit whose lines a later commit modified,
#    stopped with content conflict and REVERT_HEAD set.
#
#  Check:
#  - B4 (labels for cherry-pick: HEAD vs picked commit; labels for revert: revert of ...).
#  - B6 (Skip moves Local1 to 3rd pick).
#  - Manual check 3.

source "$SCENARIO_LIB"
lab_begin

lab_use_template _base

lab_pin_config "$SCENARIO_LOCAL1"
lab_pin_config "$SCENARIO_LOCAL2"

# ── 1. Local1: Setup cherry-pick range ──
mkdir -p "$SCENARIO_LOCAL1/src"
cat << 'EOF' > "$SCENARIO_LOCAL1/src/app.cs"
public class AppService
{
    public int Counter = 0;
}
EOF
lab_commit "$SCENARIO_LOCAL1" "Initial app service"
git -C "$SCENARIO_LOCAL1" push origin main

# Branch hotfix/1.4 with 3 commits (H1 clean, H2 conflicting, H3 pending)
git -C "$SCENARIO_LOCAL1" checkout -b hotfix/1.4 main

echo "// Hotfix 1 clean utility" > "$SCENARIO_LOCAL1/src/hotfix1.cs"
lab_commit "$SCENARIO_LOCAL1" "H1: add hotfix1 clean utility"

cat << 'EOF' > "$SCENARIO_LOCAL1/src/app.cs"
public class AppService
{
    public int Counter = 100;
}
EOF
lab_commit "$SCENARIO_LOCAL1" "H2: update counter to 100 (conflicting)"
H2_SHA="$(git -C "$SCENARIO_LOCAL1" rev-parse HEAD)"

echo "// Hotfix 3 pending utility" > "$SCENARIO_LOCAL1/src/hotfix3.cs"
lab_commit "$SCENARIO_LOCAL1" "H3: add hotfix3 pending utility"

# Switch back to main on Local1 and create conflicting edit
git -C "$SCENARIO_LOCAL1" checkout main
cat << 'EOF' > "$SCENARIO_LOCAL1/src/app.cs"
public class AppService
{
    public int Counter = 50;
}
EOF
lab_commit "$SCENARIO_LOCAL1" "Main: update counter to 50"

# Execute cherry-pick of main..hotfix/1.4 (stops at H2)
lab_expect_conflict "$SCENARIO_LOCAL1" "cherry-pick main..hotfix/1.4" cherry-pick main..hotfix/1.4

# ── 2. Local2: Setup revert conflict ──
mkdir -p "$SCENARIO_LOCAL2/src"
echo "Base revert content line 0" > "$SCENARIO_LOCAL2/src/rev.txt"
lab_commit "$SCENARIO_LOCAL2" "C0: create rev.txt"

echo "Version 1 feature content line A" > "$SCENARIO_LOCAL2/src/rev.txt"
lab_commit "$SCENARIO_LOCAL2" "C1: update rev.txt to version 1"
C1_REV="$(git -C "$SCENARIO_LOCAL2" rev-parse HEAD)"

echo "Version 2 release content line B" > "$SCENARIO_LOCAL2/src/rev.txt"
lab_commit "$SCENARIO_LOCAL2" "C2: update rev.txt to version 2"

# Revert C1 on Local2 (conflicts because HEAD has version 2 content)
lab_expect_conflict "$SCENARIO_LOCAL2" "revert C1 commit" revert "$C1_REV"

# ── 3. Self-checks ──
# Local1: cherry-pick state
lab_assert_op "$SCENARIO_LOCAL1" cherry-pick
lab_assert_status "$SCENARIO_LOCAL1" src/app.cs UU

GDIR_L1="$(_lab_resolve_git_dir "$SCENARIO_LOCAL1")"
lab_assert "Local1 has CHERRY_PICK_HEAD" [ -f "$GDIR_L1/CHERRY_PICK_HEAD" ]
lab_assert "CHERRY_PICK_HEAD matches H2" [ "$(cat "$GDIR_L1/CHERRY_PICK_HEAD")" = "$H2_SHA" ]
lab_assert "sequencer/todo has 2 picks (current and pending)" [ "$(grep -c '^pick ' "$GDIR_L1/sequencer/todo")" -eq 2 ]
lab_assert "sequencer/todo has pending pick for H3" grep -q "H3" "$GDIR_L1/sequencer/todo"

# Local2: revert state
lab_assert_op "$SCENARIO_LOCAL2" revert
lab_assert_status "$SCENARIO_LOCAL2" src/rev.txt UU

GDIR_L2="$(_lab_resolve_git_dir "$SCENARIO_LOCAL2")"
lab_assert "Local2 has REVERT_HEAD" [ -f "$GDIR_L2/REVERT_HEAD" ]
lab_assert "REVERT_HEAD matches C1" [ "$(cat "$GDIR_L2/REVERT_HEAD")" = "$C1_REV" ]

lab_done
