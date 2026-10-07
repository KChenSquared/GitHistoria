#!/usr/bin/env bash
# @short: Working tree and index with diff budget limits and binary files
# @long:
#  State:
#  - Local1: no operation in progress.
#    - big/new-45k.cs: new staged file of 45 000 lines (patch under 50k budget).
#    - big/over-budget.txt: tracked file rewritten to > 50 000 lines and > 5 MB (IsTruncated).
#    - big/Large.cs: 50 000-line tracked file with 40 scattered 1-line edits.
#    - web/app.min.js: ~1 MB single line, edited.
#    - assets/logo.png: binary change.
#    - empty/was-empty.txt (empty -> content) and empty/now-empty.txt (content -> empty).
#    - tools/run.sh: staged mode change (chmod +x).
#    - docs/old-name.md -> docs/new-name.md: staged 100% rename.
#
#  Check:
#  - B9 ("File too large to diff inline", not "No changes.").
#  - Perf and very long lines, binary summary, empty-file and mode-only edge cases.
#  - Manual checks 9, 11.
# @template: _base

source "$SCENARIO_LIB"
lab_begin

lab_use_template _base
lab_pin_config "$SCENARIO_LOCAL1"
lab_pin_config "$SCENARIO_LOCAL2"

mkdir -p "$SCENARIO_LOCAL1/big" \
         "$SCENARIO_LOCAL1/web" \
         "$SCENARIO_LOCAL1/assets" \
         "$SCENARIO_LOCAL1/empty" \
         "$SCENARIO_LOCAL1/tools" \
         "$SCENARIO_LOCAL1/docs"

# ── 1. Baseline commits ──
echo "initial baseline text for over-budget file" > "$SCENARIO_LOCAL1/big/over-budget.txt"

lab_gen_lines "$SCENARIO_LOCAL1/big/Large.cs" 50000 "LargeClassLine"

# web/app.min.js: 1 MB single line (10 000 blocks of 100 bytes)
awk 'BEGIN {
    for (i = 1; i <= 10000; i++) {
        printf("/*chunk%04d*/console.log(\"mock_payload_data_for_long_line\");", i);
    }
    printf("\n");
}' > "$SCENARIO_LOCAL1/web/app.min.js"

# assets/logo.png: binary png header
printf '\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x10\x00\x00\x00\x10\x08\x06\x00\x00\x00' > "$SCENARIO_LOCAL1/assets/logo.png"

# empty files
touch "$SCENARIO_LOCAL1/empty/was-empty.txt"
echo "had content before now empty" > "$SCENARIO_LOCAL1/empty/now-empty.txt"

# tools/run.sh (initial 0644)
echo "#!/bin/sh" > "$SCENARIO_LOCAL1/tools/run.sh"
echo "echo executable" >> "$SCENARIO_LOCAL1/tools/run.sh"

# docs/old-name.md (for 100% rename)
cat << 'EOF' > "$SCENARIO_LOCAL1/docs/old-name.md"
# Documentation Guide

This is an established documentation file with multiple paragraphs.
It will be renamed without any modifications to exercise 100% rename detection.
EOF

lab_commit "$SCENARIO_LOCAL1" "chore: add large and binary baselines"

# ── 2. Create staged new-45k.cs (< 50k lines budget) ──
lab_gen_lines "$SCENARIO_LOCAL1/big/new-45k.cs" 45000 "New45kLine"
git -C "$SCENARIO_LOCAL1" add big/new-45k.cs

# ── 3. Rewrite over-budget.txt (> 50k lines and > 5 MB) ──
lab_gen_lines "$SCENARIO_LOCAL1/big/over-budget.txt" 55000 "OverBudgetLine"

# ── 4. 40 scattered 1-line edits in Large.cs ──
awk '
{
    if (NR % 1250 == 0) {
        printf("LargeClassLine %06d modified line for navigation check\n", NR);
    } else {
        print $0;
    }
}
' "$SCENARIO_LOCAL1/big/Large.cs" > "$SCENARIO_LOCAL1/big/Large.cs.tmp"
mv "$SCENARIO_LOCAL1/big/Large.cs.tmp" "$SCENARIO_LOCAL1/big/Large.cs"

# ── 5. Edit single line in web/app.min.js ──
sed -i 's/chunk0001/chunk9999/' "$SCENARIO_LOCAL1/web/app.min.js"

# ── 6. Modify binary assets/logo.png ──
printf '\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x20\x00\x00\x00\x20\x08\x06\x00\x00\x00' > "$SCENARIO_LOCAL1/assets/logo.png"

# ── 7. Empty file transitions ──
echo "newly added content in previously empty file" > "$SCENARIO_LOCAL1/empty/was-empty.txt"
: > "$SCENARIO_LOCAL1/empty/now-empty.txt"

# ── 8. Staged mode change on tools/run.sh ──
git -C "$SCENARIO_LOCAL1" update-index --chmod=+x tools/run.sh

# ── 9. Staged 100% rename docs/old-name.md -> docs/new-name.md ──
git -C "$SCENARIO_LOCAL1" mv docs/old-name.md docs/new-name.md

# ── Self-checks ──
lab_assert_op "$SCENARIO_LOCAL1" none

NEW_LINES="$(git -C "$SCENARIO_LOCAL1" diff --cached -- big/new-45k.cs | wc -l)"
lab_assert "new-45k patch line count is < 50000" [ "$NEW_LINES" -lt 50000 ]
lab_assert "new-45k patch line count is > 45000" [ "$NEW_LINES" -gt 45000 ]

OVER_LINES="$(git -C "$SCENARIO_LOCAL1" diff -- big/over-budget.txt | wc -l)"
OVER_BYTES="$(git -C "$SCENARIO_LOCAL1" diff -- big/over-budget.txt | wc -c)"
lab_assert "over-budget patch line count is > 50000" [ "$OVER_LINES" -gt 50000 ]
lab_assert "over-budget patch byte count is > 5000000" [ "$OVER_BYTES" -gt 5000000 ]

SUMMARY="$(git -C "$SCENARIO_LOCAL1" diff --cached --summary)"
lab_assert "summary shows mode change on run.sh" [ -n "$(echo "$SUMMARY" | grep 'mode change.*tools/run.sh')" ]
lab_assert "summary shows rename on old-name.md" [ -n "$(echo "$SUMMARY" | grep 'rename docs/')" ]

NUMSTAT="$(git -C "$SCENARIO_LOCAL1" diff --numstat -- assets/logo.png)"
lab_assert "logo.png is detected as binary in diff numstat" [ -n "$(echo "$NUMSTAT" | grep '^[ -]	[ -]	assets/logo.png')" ]

lab_done
