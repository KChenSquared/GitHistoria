#!/usr/bin/env bash
# @short: Local1 mid-merge with every file-level status (AA, UD, DU, DD, AU, UA)
# @long:
#  State:
#  - Local1: mid-merge with all unmerged status codes:
#    src/both-added.txt (AA), src/delete-modify.txt (DU),
#    src/modify-delete.txt (UD), src/orig.txt (DD),
#    src/ours-name.txt (AU), src/theirs-name.txt (UA),
#    src/rename-ours.txt (UD from rename by ours + delete by theirs).
#
#  Check:
#  - Take-side decided by stage presence (Whole-file take ours/theirs;
#    take absent side removes path).
#  - File-level conflict cards for add/delete/rename conflicts.
#  - Manual check 2.

source "$SCENARIO_LIB"
lab_begin

lab_use_template _base

# ── 1. Create base files on main ──
mkdir -p "$SCENARIO_LOCAL1/src"
echo "Base content for modify-delete" > "$SCENARIO_LOCAL1/src/modify-delete.txt"
echo "Base content for delete-modify" > "$SCENARIO_LOCAL1/src/delete-modify.txt"
echo "Base content for orig rename" > "$SCENARIO_LOCAL1/src/orig.txt"
echo "Base content for rename-delete" > "$SCENARIO_LOCAL1/src/rename-delete.txt"

lab_commit "$SCENARIO_LOCAL1" "Initial commit: base files for add-delete-rename scenarios"
git -C "$SCENARIO_LOCAL1" push origin main

# ── 2. Local2 branches release/renames, edits, and pushes ──
git -C "$SCENARIO_LOCAL2" fetch origin
git -C "$SCENARIO_LOCAL2" checkout -b release/renames origin/main

git -C "$SCENARIO_LOCAL2" rm "$SCENARIO_LOCAL2/src/modify-delete.txt"
echo "Theirs modified content for delete-modify" > "$SCENARIO_LOCAL2/src/delete-modify.txt"
echo "Theirs newly added content" > "$SCENARIO_LOCAL2/src/both-added.txt"
git -C "$SCENARIO_LOCAL2" mv "$SCENARIO_LOCAL2/src/orig.txt" "$SCENARIO_LOCAL2/src/theirs-name.txt"
git -C "$SCENARIO_LOCAL2" rm "$SCENARIO_LOCAL2/src/rename-delete.txt"

lab_commit "$SCENARIO_LOCAL2" "Release edits for renames and deletes"
git -C "$SCENARIO_LOCAL2" push -u origin release/renames

# ── 3. Local1 branches feature/renames, edits, pushes, and fetches ──
git -C "$SCENARIO_LOCAL1" checkout -b feature/renames main

echo "Ours modified content for modify-delete" > "$SCENARIO_LOCAL1/src/modify-delete.txt"
git -C "$SCENARIO_LOCAL1" rm "$SCENARIO_LOCAL1/src/delete-modify.txt"
echo "Ours newly added content" > "$SCENARIO_LOCAL1/src/both-added.txt"
git -C "$SCENARIO_LOCAL1" mv "$SCENARIO_LOCAL1/src/orig.txt" "$SCENARIO_LOCAL1/src/ours-name.txt"
git -C "$SCENARIO_LOCAL1" mv "$SCENARIO_LOCAL1/src/rename-delete.txt" "$SCENARIO_LOCAL1/src/rename-ours.txt"

lab_commit "$SCENARIO_LOCAL1" "Feature edits for renames and deletes"
git -C "$SCENARIO_LOCAL1" push -u origin feature/renames
git -C "$SCENARIO_LOCAL1" fetch origin

# ── 4. Local1 runs merge ──
lab_pin_config "$SCENARIO_LOCAL1"
lab_expect_conflict "$SCENARIO_LOCAL1" "merge release/renames with add/delete/rename conflicts" merge origin/release/renames

# ── 5. Self-checks ──
lab_assert_op "$SCENARIO_LOCAL1" merge

# Exact porcelain codes from findings §2
lab_assert_status "$SCENARIO_LOCAL1" src/both-added.txt AA
lab_assert_status "$SCENARIO_LOCAL1" src/delete-modify.txt DU
lab_assert_status "$SCENARIO_LOCAL1" src/modify-delete.txt UD
lab_assert_status "$SCENARIO_LOCAL1" src/orig.txt DD
lab_assert_status "$SCENARIO_LOCAL1" src/ours-name.txt AU
lab_assert_status "$SCENARIO_LOCAL1" src/rename-ours.txt UD
lab_assert_status "$SCENARIO_LOCAL1" src/theirs-name.txt UA

# Stage presence assertions
# AA: stages 2 and 3, no stage 1
AA_STAGES="$(git -C "$SCENARIO_LOCAL1" ls-files -s src/both-added.txt | awk '{print $3}' | tr '\n' ' ' | xargs)"
lab_assert "src/both-added.txt has stages 2 and 3" [ "$AA_STAGES" = "2 3" ]

# DU: stages 1 and 3, no stage 2
DU_STAGES="$(git -C "$SCENARIO_LOCAL1" ls-files -s src/delete-modify.txt | awk '{print $3}' | tr '\n' ' ' | xargs)"
lab_assert "src/delete-modify.txt has stages 1 and 3" [ "$DU_STAGES" = "1 3" ]

# UD: stages 1 and 2, no stage 3
UD_STAGES="$(git -C "$SCENARIO_LOCAL1" ls-files -s src/modify-delete.txt | awk '{print $3}' | tr '\n' ' ' | xargs)"
lab_assert "src/modify-delete.txt has stages 1 and 2" [ "$UD_STAGES" = "1 2" ]

# DD: stage 1
DD_STAGES="$(git -C "$SCENARIO_LOCAL1" ls-files -s src/orig.txt | awk '{print $3}' | tr '\n' ' ' | xargs)"
lab_assert "src/orig.txt has stage 1" [ "$DD_STAGES" = "1" ]

# AU: stage 2
AU_STAGES="$(git -C "$SCENARIO_LOCAL1" ls-files -s src/ours-name.txt | awk '{print $3}' | tr '\n' ' ' | xargs)"
lab_assert "src/ours-name.txt has stage 2" [ "$AU_STAGES" = "2" ]

# UA: stage 3
UA_STAGES="$(git -C "$SCENARIO_LOCAL1" ls-files -s src/theirs-name.txt | awk '{print $3}' | tr '\n' ' ' | xargs)"
lab_assert "src/theirs-name.txt has stage 3" [ "$UA_STAGES" = "3" ]

lab_done
