#!/usr/bin/env bash
# @short: Mid-merge on marker edge cases (size-10, literal, CRLF, damaged)
# @long:
#  State:
#  - Local1: mid-merge with tricky marker text: size-10 markers (guide.rst),
#    Markdown setext underline (setext.md), literal 8-char runs (literal.txt),
#    CRLF line endings (crlf.cs), lone CR (lone-cr.txt), adjacent regions
#    separated by 4 lines (adjacent.txt), and 30 conflict regions (many.txt).
#  - Local2: mid-merge with damaged markers in src/many.txt (deleted ======= in
#    region 3 and deleted >>>>>>> in region 7).
#
#  Check:
#  - R5 (parser respects marker size, 8-char runs, CRLF, lone CR).
#  - M3.1 step 5a (damaged markers hint and confirm on Local2).
#  - R4 navigation over 30 regions.
#  - Manual check 4.

source "$SCENARIO_LIB"
lab_begin

lab_use_template _base

# ── 1. Setup .gitattributes and files on main in Local1 ──
cat << 'EOF' > "$SCENARIO_LOCAL1/.gitattributes"
*.rst conflict-marker-size=10
src/crlf.cs -text
src/lone-cr.txt -text
EOF

mkdir -p "$SCENARIO_LOCAL1/docs" "$SCENARIO_LOCAL1/src"

cat << 'EOF' > "$SCENARIO_LOCAL1/docs/guide.rst"
Guide Title
===========
Initial introductory overview for the reST guide.
=======
More text after the underline.
EOF

cat << 'EOF' > "$SCENARIO_LOCAL1/docs/setext.md"
Heading One
===========
Paragraph text under setext heading in Markdown.
=======
Alternative setext divider.
EOF

cat << 'EOF' > "$SCENARIO_LOCAL1/src/literal.txt"
Literal Marker Test
<<<<<<<<
Fake marker line with 8 chars
>>>>>>>>
Context line 1
Context line 2
Context line 3
Context line 4
Context line 5
Conflicted section base
EOF

printf 'public class CrlfClass\r\n{\r\n    public int Counter = 1;\r\n}\r\n' > "$SCENARIO_LOCAL1/src/crlf.cs"
printf 'line 1\nline 2\nline 3\n' > "$SCENARIO_LOCAL1/src/lone-cr.txt"

cat << 'EOF' > "$SCENARIO_LOCAL1/src/adjacent.txt"
Region 1 base
common separator line 1
common separator line 2
common separator line 3
common separator line 4
Region 2 base
EOF

{
    for i in $(seq 1 30); do
        echo "Region $i base text"
        echo "common line 1 for region $i"
        echo "common line 2 for region $i"
        echo "common line 3 for region $i"
        echo "common line 4 for region $i"
    done
} > "$SCENARIO_LOCAL1/src/many.txt"

lab_commit "$SCENARIO_LOCAL1" "Initial commit: edge-case marker files"
git -C "$SCENARIO_LOCAL1" push origin main

# ── 2. Local2 branches release/edge-cases, edits, and pushes ──
git -C "$SCENARIO_LOCAL2" fetch origin
git -C "$SCENARIO_LOCAL2" checkout -b release/edge-cases origin/main

cat << 'EOF' > "$SCENARIO_LOCAL2/docs/guide.rst"
Guide Title
===========
Theirs introductory overview for the reST guide.
=======
More text after the underline.
EOF

cat << 'EOF' > "$SCENARIO_LOCAL2/docs/setext.md"
Heading One
===========
Theirs paragraph text under setext heading in Markdown.
=======
Alternative setext divider.
EOF

cat << 'EOF' > "$SCENARIO_LOCAL2/src/literal.txt"
Literal Marker Test
<<<<<<<<
Fake marker line with 8 chars
>>>>>>>>
Context line 1
Context line 2
Context line 3
Context line 4
Context line 5
Conflicted section theirs
EOF

printf 'public class CrlfClass\r\n{\r\n    public int Counter = 3;\r\n}\r\n' > "$SCENARIO_LOCAL2/src/crlf.cs"
printf 'line 1\ntheirs line 2\nline 3\n' > "$SCENARIO_LOCAL2/src/lone-cr.txt"

cat << 'EOF' > "$SCENARIO_LOCAL2/src/adjacent.txt"
Region 1 theirs
common separator line 1
common separator line 2
common separator line 3
common separator line 4
Region 2 theirs
EOF

{
    for i in $(seq 1 30); do
        echo "Region $i theirs text"
        echo "common line 1 for region $i"
        echo "common line 2 for region $i"
        echo "common line 3 for region $i"
        echo "common line 4 for region $i"
    done
} > "$SCENARIO_LOCAL2/src/many.txt"

lab_commit "$SCENARIO_LOCAL2" "Release branch marker edge-case edits"
git -C "$SCENARIO_LOCAL2" push -u origin release/edge-cases

# ── 3. Local1 branches feature/marker-tests, edits, pushes, fetches ──
git -C "$SCENARIO_LOCAL1" checkout -b feature/marker-tests main

cat << 'EOF' > "$SCENARIO_LOCAL1/docs/guide.rst"
Guide Title
===========
Ours introductory overview for the reST guide.
=======
More text after the underline.
EOF

cat << 'EOF' > "$SCENARIO_LOCAL1/docs/setext.md"
Heading One
===========
Ours paragraph text under setext heading in Markdown.
=======
Alternative setext divider.
EOF

cat << 'EOF' > "$SCENARIO_LOCAL1/src/literal.txt"
Literal Marker Test
<<<<<<<<
Fake marker line with 8 chars
>>>>>>>>
Context line 1
Context line 2
Context line 3
Context line 4
Context line 5
Conflicted section ours
EOF

printf 'public class CrlfClass\r\n{\r\n    public int Counter = 2;\r\n}\r\n' > "$SCENARIO_LOCAL1/src/crlf.cs"
printf 'line 1\nours with\rCR byte inside\nline 3\n' > "$SCENARIO_LOCAL1/src/lone-cr.txt"

cat << 'EOF' > "$SCENARIO_LOCAL1/src/adjacent.txt"
Region 1 ours
common separator line 1
common separator line 2
common separator line 3
common separator line 4
Region 2 ours
EOF

{
    for i in $(seq 1 30); do
        echo "Region $i ours text"
        echo "common line 1 for region $i"
        echo "common line 2 for region $i"
        echo "common line 3 for region $i"
        echo "common line 4 for region $i"
    done
} > "$SCENARIO_LOCAL1/src/many.txt"

lab_commit "$SCENARIO_LOCAL1" "Feature branch marker edge-case edits"
git -C "$SCENARIO_LOCAL1" push -u origin feature/marker-tests
git -C "$SCENARIO_LOCAL1" fetch origin

# ── 4. Local1 runs merge ──
lab_pin_config "$SCENARIO_LOCAL1"
lab_expect_conflict "$SCENARIO_LOCAL1" "merge release/edge-cases into feature/marker-tests" merge origin/release/edge-cases

# ── 5. Local2 checks out feature/marker-tests and runs merge, then damages markers ──
git -C "$SCENARIO_LOCAL2" fetch origin
git -C "$SCENARIO_LOCAL2" checkout -b feature/marker-tests origin/feature/marker-tests
lab_pin_config "$SCENARIO_LOCAL2"
lab_expect_conflict "$SCENARIO_LOCAL2" "merge release/edge-cases in Local2" merge origin/release/edge-cases

# Damage markers in Local2 src/many.txt: delete region 3's ======= line and region 7's >>>>>>> line
awk '
/^=======/ {
    eq_count++
    if (eq_count == 3) next
}
/^>>>>>>>/ {
    gt_count++
    if (gt_count == 7) next
}
{ print }
' "$SCENARIO_LOCAL2/src/many.txt" > "$SCENARIO_LOCAL2/src/many.txt.tmp"
mv "$SCENARIO_LOCAL2/src/many.txt.tmp" "$SCENARIO_LOCAL2/src/many.txt"

# ── 6. Self-checks ──
lab_assert_op "$SCENARIO_LOCAL1" merge
lab_assert_op "$SCENARIO_LOCAL2" merge

# docs/guide.rst: size 10 is 1, size 7 is 0
lab_assert "guide.rst has exactly 1 size-10 marker" [ "$(lab_count_markers "$SCENARIO_LOCAL1/docs/guide.rst" '<' 10)" -eq 1 ]
lab_assert "guide.rst has 0 size-7 markers" [ "$(lab_count_markers "$SCENARIO_LOCAL1/docs/guide.rst" '<' 7)" -eq 0 ]

# src/literal.txt has exactly 1 real region (8-char runs are not counted)
lab_assert "literal.txt counts exactly 1 real region" [ "$(lab_count_markers "$SCENARIO_LOCAL1/src/literal.txt" '<' 7)" -eq 1 ]

# src/crlf.cs marker lines end in \r
lab_assert "crlf.cs marker lines end in CR" grep -U -q $'^<<<<<<< .*\r$' "$SCENARIO_LOCAL1/src/crlf.cs"

# src/lone-cr.txt contains a \r not followed by \n
lab_assert "lone-cr.txt contains lone CR byte" awk '{ sub(/\r$/, ""); if (index($0, "\r") > 0) found=1 } END { exit (found ? 0 : 1) }' "$SCENARIO_LOCAL1/src/lone-cr.txt"

# src/adjacent.txt has exactly 2 regions separated by 4 lines
lab_assert "adjacent.txt has 2 regions" [ "$(lab_count_markers "$SCENARIO_LOCAL1/src/adjacent.txt" '<' 7)" -eq 2 ]

# Local1 src/many.txt has 30 regions
lab_assert "Local1 many.txt has 30 regions" [ "$(lab_count_markers "$SCENARIO_LOCAL1/src/many.txt" '<' 7)" -eq 30 ]

# Local2 src/many.txt has 30 start markers, 29 = markers, 29 > markers
lab_assert "Local2 many.txt has 30 start markers" [ "$(lab_count_markers "$SCENARIO_LOCAL2/src/many.txt" '<' 7)" -eq 30 ]
lab_assert "Local2 many.txt has 29 = markers" [ "$(lab_count_markers "$SCENARIO_LOCAL2/src/many.txt" '=' 7)" -eq 29 ]
lab_assert "Local2 many.txt has 29 > markers" [ "$(lab_count_markers "$SCENARIO_LOCAL2/src/many.txt" '>' 7)" -eq 29 ]

lab_done
