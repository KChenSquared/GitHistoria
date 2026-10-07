#!/usr/bin/env bash
# @short: Submodule pointer conflict (UU in Local1; UD in Local2)
# @long:
#  State:
#  - Local1: mid-merge with submodule libs/widget pointing to divergent commits (UU),
#    with stages 1, 2, 3 present at mode 160000; libs/widget/.git exists; README.md conflicted.
#  - Local2: mid-merge where theirs deleted the submodule and ours modified it (UD).
#
#  Check:
#  - B14 (take theirs stages theirs' sha at stage 0 and leaves the folder; the absent
#    side does rm --cached, never -f), file-level card for submodules.
#  - Manual check 2.

source "$SCENARIO_LIB"
lab_begin

# ── 1. Create bare widget repository and seed divergent commits ──
lab_bare widget
CLONE_WIDGET="$SCENARIO_WORK/widget-seed"
git clone -q "$SCENARIO_REMOTES/widget" "$CLONE_WIDGET"
git -C "$CLONE_WIDGET" branch -M main

echo "Widget Base v1.0" > "$CLONE_WIDGET/README.md"
lab_commit "$CLONE_WIDGET" "Initial widget commit"
W_BASE="$(git -C "$CLONE_WIDGET" rev-parse HEAD)"

git -C "$CLONE_WIDGET" checkout -b branch-ours main
echo "Widget Ours v1.1" > "$CLONE_WIDGET/README.md"
lab_commit "$CLONE_WIDGET" "Widget ours update (o1)"
W_OURS="$(git -C "$CLONE_WIDGET" rev-parse HEAD)"

git -C "$CLONE_WIDGET" checkout main
git -C "$CLONE_WIDGET" checkout -b branch-theirs main
echo "Widget Theirs v1.2" > "$CLONE_WIDGET/README.md"
lab_commit "$CLONE_WIDGET" "Widget theirs update (t1)"
W_THEIRS="$(git -C "$CLONE_WIDGET" rev-parse HEAD)"

git -C "$CLONE_WIDGET" push -q --all origin

# ── 2. Initialize main repository from _base ──
lab_use_template _base

# Pin protocol.file.allow=always on Local1 and Local2
lab_pin_config "$SCENARIO_LOCAL1" protocol.file.allow always
lab_pin_config "$SCENARIO_LOCAL2" protocol.file.allow always

# Add submodule libs/widget on Local1 main
git -C "$SCENARIO_LOCAL1" -c protocol.file.allow=always submodule add ../widget libs/widget
echo "Main Application Base" > "$SCENARIO_LOCAL1/README.md"
lab_commit "$SCENARIO_LOCAL1" "Add libs/widget submodule at base"
git -C "$SCENARIO_LOCAL1" push origin main

# ── 3. Local1: create release/2.0 branch moving widget to t1 ──
git -C "$SCENARIO_LOCAL1" checkout -b release/2.0 main
(cd "$SCENARIO_LOCAL1/libs/widget" && git fetch -q origin && git checkout -q "$W_THEIRS")
git -C "$SCENARIO_LOCAL1" add libs/widget
echo "Main Application Theirs Release" > "$SCENARIO_LOCAL1/README.md"
lab_commit "$SCENARIO_LOCAL1" "Update widget to t1 and release notes"
git -C "$SCENARIO_LOCAL1" push -u origin release/2.0

# ── 4. Local1: switch back to main, move widget to o1, merge release/2.0 ──
git -C "$SCENARIO_LOCAL1" checkout main
(cd "$SCENARIO_LOCAL1/libs/widget" && git fetch -q origin && git checkout -q "$W_OURS")
git -C "$SCENARIO_LOCAL1" add libs/widget
echo "Main Application Ours Feature" > "$SCENARIO_LOCAL1/README.md"
lab_commit "$SCENARIO_LOCAL1" "Update widget to o1 and feature notes"
git -C "$SCENARIO_LOCAL1" push origin main

lab_expect_conflict "$SCENARIO_LOCAL1" "merge release/2.0 with submodule conflict" merge origin/release/2.0

# ── 5. Local2: create mid-merge where theirs deleted submodule (UD) ──
git -C "$SCENARIO_LOCAL2" fetch origin
git -C "$SCENARIO_LOCAL2" checkout main
git -C "$SCENARIO_LOCAL2" reset --hard origin/main
git -C "$SCENARIO_LOCAL2" -c protocol.file.allow=always submodule update --init -q

# Branch drop-widget from main~1 (the commit that added submodule base, or main^ before update)
git -C "$SCENARIO_LOCAL2" checkout -b drop-widget origin/main~1
git -C "$SCENARIO_LOCAL2" rm -f libs/widget
lab_commit "$SCENARIO_LOCAL2" "Remove widget submodule"

# Back to main (which has widget moved to o1) and merge drop-widget
git -C "$SCENARIO_LOCAL2" checkout main
lab_expect_conflict "$SCENARIO_LOCAL2" "merge drop-widget into main" merge drop-widget

# ── 6. Self-checks ──
# Local1: operation is merge, status UU, stages 1 2 3 in mode 160000, .git exists
lab_assert_op "$SCENARIO_LOCAL1" merge
lab_assert_status "$SCENARIO_LOCAL1" libs/widget UU
lab_assert_status "$SCENARIO_LOCAL1" README.md UU

STAGES_LOCAL1="$(git -C "$SCENARIO_LOCAL1" ls-files -s libs/widget)"
lab_assert "libs/widget has three 160000 stage entries" [ "$(echo "$STAGES_LOCAL1" | grep -c '^160000 ')" -eq 3 ]
lab_assert "libs/widget has stage 1" grep -q ' 1[[:space:]]libs/widget' <(git -C "$SCENARIO_LOCAL1" ls-files -s libs/widget)
lab_assert "libs/widget has stage 2" grep -q ' 2[[:space:]]libs/widget' <(git -C "$SCENARIO_LOCAL1" ls-files -s libs/widget)
lab_assert "libs/widget has stage 3" grep -q ' 3[[:space:]]libs/widget' <(git -C "$SCENARIO_LOCAL1" ls-files -s libs/widget)
lab_assert "libs/widget/.git exists" [ -e "$SCENARIO_LOCAL1/libs/widget/.git" ]

# Local2: operation is merge, status UD with 160000 entries
lab_assert_op "$SCENARIO_LOCAL2" merge
lab_assert_status "$SCENARIO_LOCAL2" libs/widget UD
STAGES_LOCAL2="$(git -C "$SCENARIO_LOCAL2" ls-files -s libs/widget)"
lab_assert "Local2 libs/widget has two 160000 stage entries (stages 1 and 2)" [ "$(echo "$STAGES_LOCAL2" | grep -c '^160000 ')" -eq 2 ]

lab_done
