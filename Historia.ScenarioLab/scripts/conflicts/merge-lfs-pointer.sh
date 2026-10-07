#!/usr/bin/env bash
# @short: Local1 mid-merge on Git LFS pointer conflict (UU)
# @long:
#  State:
#  - Local1: mid-merge on an LFS-tracked file art/cover.psd where conflict markers
#    sit inside the LFS pointer text (between two oid sha256: lines).
#    Also includes docs/notes.txt with standard text conflict regions.
#  - No push to remote (local file path remotes do not run an LFS HTTP endpoint).
#
#  Check:
#  - B2 (Take theirs writes real content via smudge filter, not raw pointer text).
#  - B7 (External tool receives smudged real content).
#  - Manual check 2.

source "$SCENARIO_LIB"
lab_begin

# Require git-lfs binary before proceeding
lab_require git-lfs "git-lfs not installed"

lab_use_template _base

# ── 1. Configure Git LFS locally on Local1 ──
# Local installation only; do not push to remote as local file remotes have no LFS endpoint.
git -C "$SCENARIO_LOCAL1" lfs install --local
git -C "$SCENARIO_LOCAL1" lfs track "*.psd"
git -C "$SCENARIO_LOCAL1" add .gitattributes

mkdir -p "$SCENARIO_LOCAL1/art" "$SCENARIO_LOCAL1/docs"
echo "Cover Art Base Payload v1.0" > "$SCENARIO_LOCAL1/art/cover.psd"
echo "Base design documentation" > "$SCENARIO_LOCAL1/docs/notes.txt"

git -C "$SCENARIO_LOCAL1" add art/ docs/
lab_commit "$SCENARIO_LOCAL1" "Initial commit with LFS-tracked PSD asset"

# ── 2. Branch art/update and modify asset ──
git -C "$SCENARIO_LOCAL1" checkout -b art/update main
echo "Cover Art Theirs Payload v2.0 (Theirs)" > "$SCENARIO_LOCAL1/art/cover.psd"
echo "Theirs design notes update" > "$SCENARIO_LOCAL1/docs/notes.txt"
lab_commit "$SCENARIO_LOCAL1" "Update cover PSD on art/update"

# ── 3. Switch back to main, modify asset differently, and merge ──
git -C "$SCENARIO_LOCAL1" checkout main
echo "Cover Art Ours Payload v2.0 (Ours)" > "$SCENARIO_LOCAL1/art/cover.psd"
echo "Ours design notes update" > "$SCENARIO_LOCAL1/docs/notes.txt"
lab_commit "$SCENARIO_LOCAL1" "Update cover PSD on main"

lab_pin_config "$SCENARIO_LOCAL1"
lab_expect_conflict "$SCENARIO_LOCAL1" "merge art/update into main with LFS conflict" merge art/update

# ── 4. Self-checks ──
lab_assert_op "$SCENARIO_LOCAL1" merge
lab_assert_status "$SCENARIO_LOCAL1" art/cover.psd UU
lab_assert_status "$SCENARIO_LOCAL1" docs/notes.txt UU

# Working file starts with 'version '
lab_assert "cover.psd starts with LFS version header" grep -q '^version ' "$SCENARIO_LOCAL1/art/cover.psd"

# Holds conflict region between two oid sha256 lines
lab_assert "cover.psd contains conflict marker <<<<<<<" grep -q '^<<<<<<<' "$SCENARIO_LOCAL1/art/cover.psd"
lab_assert "cover.psd contains oid sha256: lines" [ "$(grep -c '^oid sha256:' "$SCENARIO_LOCAL1/art/cover.psd")" -ge 2 ]

# git lfs ls-files lists art/cover.psd
lab_assert "git lfs ls-files lists art/cover.psd" grep -q "art/cover.psd" <(git -C "$SCENARIO_LOCAL1" lfs ls-files)

lab_done
