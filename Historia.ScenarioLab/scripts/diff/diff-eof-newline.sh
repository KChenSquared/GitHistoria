#!/usr/bin/env bash
# @short: Working tree and index with end-of-file newline variants
# @long:
#  State:
#  - Local1: no operation in progress.
#    - eof/a-append.txt: x\na -> x\na\nb (no EOL on either side, 2 "No newline" lines).
#    - eof/b-change-last.txt: last line changed without EOL (2 "No newline" lines).
#    - eof/c-add-newline.txt: gains trailing newline (1 "No newline" line).
#    - eof/d-remove-newline.txt: loses trailing newline (1 "No newline" line).
#    - eof/e-staged-append.txt: case (a) staged (status M , 2 "No newline" lines).
#
#  Check:
#  - B16 (selecting only +b warns and stages nothing; -a +a +b together stage
#    x\na\nb; the reverse mirror warns).
#  - Manual check 9.
# @template: _base

source "$SCENARIO_LIB"
lab_begin

lab_use_template _base
lab_pin_config "$SCENARIO_LOCAL1"
lab_pin_config "$SCENARIO_LOCAL2"

mkdir -p "$SCENARIO_LOCAL1/eof"

# ── 1. Baseline commits ──
printf "x\na" > "$SCENARIO_LOCAL1/eof/a-append.txt"
printf "line1\noldlast" > "$SCENARIO_LOCAL1/eof/b-change-last.txt"
printf "line1\nline2" > "$SCENARIO_LOCAL1/eof/c-add-newline.txt"
printf "line1\nline2\n" > "$SCENARIO_LOCAL1/eof/d-remove-newline.txt"
printf "x\na" > "$SCENARIO_LOCAL1/eof/e-staged-append.txt"

lab_commit "$SCENARIO_LOCAL1" "chore: add eof newline test baselines"

# ── 2. Working tree and index modifications ──
# eof/a-append.txt: no EOL on either side (append b)
printf "x\na\nb" > "$SCENARIO_LOCAL1/eof/a-append.txt"

# eof/b-change-last.txt: no EOL on either side (change last line)
printf "line1\nnewlast" > "$SCENARIO_LOCAL1/eof/b-change-last.txt"

# eof/c-add-newline.txt: gains trailing newline
printf "line1\nline2\n" > "$SCENARIO_LOCAL1/eof/c-add-newline.txt"

# eof/d-remove-newline.txt: loses trailing newline
printf "line1\nline2" > "$SCENARIO_LOCAL1/eof/d-remove-newline.txt"

# eof/e-staged-append.txt: staged case (a)
printf "x\na\nb" > "$SCENARIO_LOCAL1/eof/e-staged-append.txt"
git -C "$SCENARIO_LOCAL1" add eof/e-staged-append.txt

# ── Self-checks ──
lab_assert_op "$SCENARIO_LOCAL1" none
lab_assert "a-append.txt has 2 No newline lines" [ "$(git -C "$SCENARIO_LOCAL1" diff -- eof/a-append.txt | grep -c 'No newline at end of file')" -eq 2 ]
lab_assert "b-change-last.txt has 2 No newline lines" [ "$(git -C "$SCENARIO_LOCAL1" diff -- eof/b-change-last.txt | grep -c 'No newline at end of file')" -eq 2 ]
lab_assert "c-add-newline.txt has 1 No newline line" [ "$(git -C "$SCENARIO_LOCAL1" diff -- eof/c-add-newline.txt | grep -c 'No newline at end of file')" -eq 1 ]
lab_assert "d-remove-newline.txt has 1 No newline line" [ "$(git -C "$SCENARIO_LOCAL1" diff -- eof/d-remove-newline.txt | grep -c 'No newline at end of file')" -eq 1 ]
lab_assert "e-staged-append.txt has 2 No newline lines in cached diff" [ "$(git -C "$SCENARIO_LOCAL1" diff --cached -- eof/e-staged-append.txt | grep -c 'No newline at end of file')" -eq 2 ]

lab_assert_status "$SCENARIO_LOCAL1" eof/e-staged-append.txt "M "
lab_assert_status "$SCENARIO_LOCAL1" eof/a-append.txt " M"
lab_assert_status "$SCENARIO_LOCAL1" eof/b-change-last.txt " M"
lab_assert_status "$SCENARIO_LOCAL1" eof/c-add-newline.txt " M"
lab_assert_status "$SCENARIO_LOCAL1" eof/d-remove-newline.txt " M"

lab_done
