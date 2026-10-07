#!/usr/bin/env bash
# @short: Local1 mid-merge at scale (45k lines, over-budget, 1MB long line)
# @long:
#  State:
#  - Local1: mid-merge on feature/large-rework with 3 conflicted files:
#    src/Generated/Large.cs (45,000 lines, 200 conflict regions, under 50k lines
#    and under 5MB budget including markers), data/over-budget.log (60,000 lines,
#    3 regions, over 50k lines and over 5MB), and web/vendor.min.js (1 region,
#    single line > 1MB).
#
#  Check:
#  - Resolver size budget (over-budget file displays file-level actions only).
#  - Long line handling without UI degradation.
#  - Navigation and editor scrolling performance across 200 regions.
#  - Manual check 11.

source "$SCENARIO_LIB"
lab_begin

lab_use_template _base

# ── 1. Local1 creates initial files on main ──
mkdir -p "$SCENARIO_LOCAL1/src/Generated" "$SCENARIO_LOCAL1/data" "$SCENARIO_LOCAL1/web"

lab_gen_lines "$SCENARIO_LOCAL1/src/Generated/Large.cs" 45000 "LargeLine"
lab_gen_lines "$SCENARIO_LOCAL1/data/over-budget.log" 60000 "LogRecord"

awk 'BEGIN {
    printf "var vendorBase = \""
    for (i = 0; i < 1050000; i++) printf "x"
    printf "\";\n"
}' > "$SCENARIO_LOCAL1/web/vendor.min.js"

lab_commit "$SCENARIO_LOCAL1" "Initial commit: large files and long lines"
git -C "$SCENARIO_LOCAL1" push origin main

# ── 2. Local2 branches release/large-updates, edits, and pushes ──
git -C "$SCENARIO_LOCAL2" fetch origin
git -C "$SCENARIO_LOCAL2" checkout -b release/large-updates origin/main

awk '
NR % 225 == 0 { sub(/fixed filler.*/, "theirs conflict edit"); }
{ print }
' "$SCENARIO_LOCAL2/src/Generated/Large.cs" > "$SCENARIO_LOCAL2/src/Generated/Large.cs.tmp"
mv "$SCENARIO_LOCAL2/src/Generated/Large.cs.tmp" "$SCENARIO_LOCAL2/src/Generated/Large.cs"

awk '
NR == 1000 || NR == 20000 || NR == 50000 { sub(/fixed filler.*/, "theirs conflict edit"); }
{ print }
' "$SCENARIO_LOCAL2/data/over-budget.log" > "$SCENARIO_LOCAL2/data/over-budget.log.tmp"
mv "$SCENARIO_LOCAL2/data/over-budget.log.tmp" "$SCENARIO_LOCAL2/data/over-budget.log"

awk 'BEGIN {
    printf "var vendorTheirs = \""
    for (i = 0; i < 1050000; i++) printf "y"
    printf "\";\n"
}' > "$SCENARIO_LOCAL2/web/vendor.min.js"

lab_commit "$SCENARIO_LOCAL2" "Release updates for large files"
git -C "$SCENARIO_LOCAL2" push -u origin release/large-updates

# ── 3. Local1 branches feature/large-rework, edits, pushes, fetches ──
git -C "$SCENARIO_LOCAL1" checkout -b feature/large-rework main

awk '
NR % 225 == 0 { sub(/fixed filler.*/, "ours conflict edit"); }
{ print }
' "$SCENARIO_LOCAL1/src/Generated/Large.cs" > "$SCENARIO_LOCAL1/src/Generated/Large.cs.tmp"
mv "$SCENARIO_LOCAL1/src/Generated/Large.cs.tmp" "$SCENARIO_LOCAL1/src/Generated/Large.cs"

awk '
NR == 1000 || NR == 20000 || NR == 50000 { sub(/fixed filler.*/, "ours conflict edit"); }
{ print }
' "$SCENARIO_LOCAL1/data/over-budget.log" > "$SCENARIO_LOCAL1/data/over-budget.log.tmp"
mv "$SCENARIO_LOCAL1/data/over-budget.log.tmp" "$SCENARIO_LOCAL1/data/over-budget.log"

awk 'BEGIN {
    printf "var vendorOurs = \""
    for (i = 0; i < 1050000; i++) printf "z"
    printf "\";\n"
}' > "$SCENARIO_LOCAL1/web/vendor.min.js"

lab_commit "$SCENARIO_LOCAL1" "Feature rework updates for large files"
git -C "$SCENARIO_LOCAL1" push -u origin feature/large-rework
git -C "$SCENARIO_LOCAL1" fetch origin

# ── 4. Local1 runs merge ──
lab_pin_config "$SCENARIO_LOCAL1"
lab_expect_conflict "$SCENARIO_LOCAL1" "merge release/large-updates into feature/large-rework" merge origin/release/large-updates

# ── 5. Self-checks ──
lab_assert_op "$SCENARIO_LOCAL1" merge

# Large.cs: 200 regions, stays under 50,000 lines and under 5MB (5,242,880 bytes) including markers
lab_assert "Large.cs has 200 conflict regions" [ "$(lab_count_markers "$SCENARIO_LOCAL1/src/Generated/Large.cs" '<' 7)" -eq 200 ]
lab_assert "Large.cs stays under 50,000 lines" [ "$(wc -l < "$SCENARIO_LOCAL1/src/Generated/Large.cs")" -lt 50000 ]
lab_assert "Large.cs stays under 5MB" [ "$(wc -c < "$SCENARIO_LOCAL1/src/Generated/Large.cs")" -lt 5242880 ]

# over-budget.log: 3 regions, exceeds 50,000 lines and exceeds 5MB
lab_assert "over-budget.log has 3 conflict regions" [ "$(lab_count_markers "$SCENARIO_LOCAL1/data/over-budget.log" '<' 7)" -eq 3 ]
lab_assert "over-budget.log exceeds 50,000 lines" [ "$(wc -l < "$SCENARIO_LOCAL1/data/over-budget.log")" -gt 50000 ]
lab_assert "over-budget.log exceeds 5MB" [ "$(wc -c < "$SCENARIO_LOCAL1/data/over-budget.log")" -gt 5242880 ]

# vendor.min.js: 1 region, longest line exceeds 1,000,000 bytes
lab_assert "vendor.min.js has 1 conflict region" [ "$(lab_count_markers "$SCENARIO_LOCAL1/web/vendor.min.js" '<' 7)" -eq 1 ]
lab_assert "vendor.min.js longest line exceeds 1MB" [ "$(wc -L < "$SCENARIO_LOCAL1/web/vendor.min.js")" -gt 1000000 ]

lab_done
