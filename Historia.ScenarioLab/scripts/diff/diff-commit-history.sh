#!/usr/bin/env bash
# @short: Commit history diffs with renames, mode changes, and merge commits
# @long:
#  State:
#  - Local1: no operation in progress; working tree and index clean.
#    - Commit 1: rename with edits (R0..R99).
#    - Commit 2: mode change (chmod +x).
#    - Commit 3: whitespace-only edit.
#    - Commit 4: 40 files at once.
#    - Commit 5: binary change.
#    - Commit 6: CJK and emoji inline edit (non-ASCII inline diff).
#    - Commit 7: file over diff budget (> 50k lines, > 5 MB).
#    - Commit 8: two-parent merge commit modifying merge-notes.txt.
#
#  Check:
#  - Commit diffs show no staging actions.
#  - Truncated and binary commit diffs.
#  - Merge commit diff (vs parent N).
#  - Manual check 9.
# @template: _base

source "$SCENARIO_LIB"
lab_begin

lab_use_template _base
lab_pin_config "$SCENARIO_LOCAL1"
lab_pin_config "$SCENARIO_LOCAL2"

mkdir -p "$SCENARIO_LOCAL1/docs" \
         "$SCENARIO_LOCAL1/tools" \
         "$SCENARIO_LOCAL1/src" \
         "$SCENARIO_LOCAL1/assets" \
         "$SCENARIO_LOCAL1/notes"

# ── Baseline setup ──
cat << 'EOF' > "$SCENARIO_LOCAL1/docs/rename-me.md"
Line 1: preamble
Line 2: body content
Line 3: details
Line 4: more details
Line 5: conclusion
EOF

echo "echo run" > "$SCENARIO_LOCAL1/tools/script.sh"
printf "int x = 1;\nint y = 2;\n" > "$SCENARIO_LOCAL1/src/clean.cs"
printf '\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x10\x00\x00\x00\x10\x08\x06\x00\x00\x00' > "$SCENARIO_LOCAL1/assets/logo.png"
printf 'Hello \xe4\xb8\x96\xe7\x95\x8c \xf0\x9f\x9a\x80 Welcome!\n' > "$SCENARIO_LOCAL1/src/unicode.txt"
echo "small budget baseline" > "$SCENARIO_LOCAL1/src/budget.txt"
echo "Initial notes" > "$SCENARIO_LOCAL1/notes/merge-notes.txt"

lab_commit "$SCENARIO_LOCAL1" "chore: baseline commit for history tests"

# ── Commit 1: Rename with edits ──
git -C "$SCENARIO_LOCAL1" mv docs/rename-me.md docs/renamed-doc.md
echo "Line 6: added conclusion" >> "$SCENARIO_LOCAL1/docs/renamed-doc.md"
lab_commit "$SCENARIO_LOCAL1" "refactor: rename document with edits"

# ── Commit 2: Mode change ──
git -C "$SCENARIO_LOCAL1" update-index --chmod=+x tools/script.sh
lab_tick
git -C "$SCENARIO_LOCAL1" commit -m "chore: make script executable"

# ── Commit 3: Whitespace-only edit ──
printf "int x = 1;   \nint y = 2;\t\n" > "$SCENARIO_LOCAL1/src/clean.cs"
lab_commit "$SCENARIO_LOCAL1" "style: whitespace adjustments"

# ── Commit 4: 40 files at once ──
mkdir -p "$SCENARIO_LOCAL1/batch"
for i in $(seq -w 1 40); do
    echo "batch file $i content" > "$SCENARIO_LOCAL1/batch/file_$i.txt"
done
lab_commit "$SCENARIO_LOCAL1" "feat: add 40 batch files"

# ── Commit 5: Binary change ──
printf '\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x20\x00\x00\x00\x20\x08\x06\x00\x00\x00' > "$SCENARIO_LOCAL1/assets/logo.png"
lab_commit "$SCENARIO_LOCAL1" "assets: update logo binary"

# ── Commit 6: CJK and emoji inline edit ──
printf 'Hello \xe5\xae\x87\xe5\xae\x99 \xf0\x9f\x9a\x80 Welcome!\n' > "$SCENARIO_LOCAL1/src/unicode.txt"
lab_commit "$SCENARIO_LOCAL1" "i18n: update greeting with CJK and emoji"

# ── Commit 7: File over diff budget (> 50k lines, > 5 MB) ──
lab_gen_lines "$SCENARIO_LOCAL1/src/budget.txt" 55000 "OverBudgetHistory"
lab_commit "$SCENARIO_LOCAL1" "perf: exceed diff line and byte budget"

# ── Commit 8: Two-parent merge commit modifying merge-notes.txt ──
git -C "$SCENARIO_LOCAL1" checkout -b feature/merge-notes
echo "Notes from feature branch" > "$SCENARIO_LOCAL1/notes/feature.txt"
lab_commit "$SCENARIO_LOCAL1" "feat: branch feature notes"

git -C "$SCENARIO_LOCAL1" checkout main
echo "Notes from main branch" > "$SCENARIO_LOCAL1/notes/main.txt"
lab_commit "$SCENARIO_LOCAL1" "feat: main branch notes"

git -C "$SCENARIO_LOCAL1" merge --no-ff --no-commit feature/merge-notes
echo "Resolved merge notes differing from both parents" > "$SCENARIO_LOCAL1/notes/merge-notes.txt"
git -C "$SCENARIO_LOCAL1" add notes/merge-notes.txt
lab_tick
git -C "$SCENARIO_LOCAL1" commit -m "merge: integrate feature/merge-notes"

# ── Self-checks ──
lab_assert_op "$SCENARIO_LOCAL1" none
lab_assert "Working tree and index are clean" [ -z "$(git -C "$SCENARIO_LOCAL1" status --porcelain)" ]
lab_assert "Merge commit has 2 parents" git -C "$SCENARIO_LOCAL1" rev-parse --verify "HEAD^2"

RENAME_COMMIT="$(git -C "$SCENARIO_LOCAL1" log --grep='rename document with edits' --format='%H' -n 1)"
lab_assert "Rename commit shows R in name-status" [ -n "$(git -C "$SCENARIO_LOCAL1" show --name-status -M "$RENAME_COMMIT" | grep '^R')" ]

lab_done
