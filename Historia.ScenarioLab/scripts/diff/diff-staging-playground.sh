#!/usr/bin/env bash
# @short: Working tree and index with every staging shape and multi-hunk edits
# @long:
#  State:
#  - Local1: no operation in progress.
#    - src/Orders/OrderService.cs: ~300 lines with 12 unstaged hunks (replacements,
#      one-word edits, pure additions, pure deletions).
#    - src/Orders/OrderValidator.cs: partially staged (hunks 1-2 staged with mixed
#      add/delete, hunks 3-4 unstaged, status MM).
#    - src/Tokenizer/Comments.cs: hunk deletes line opening /*, another holds unmatched ".
#    - lang/: small 2-hunk edits in .py .ts .json .xml .axaml .md .toml .kt .h .yml .sh
#      and sample.unknownext.
#    - docs/long-lines.md: one prose line > 300 chars edited (DiffWordWrap).
#    - Staged rename with edit: src/Legacy/Old.cs -> src/Core/New.cs.
#    - Staged new file: src/NewFeature.cs.
#    - Unstaged deletion: src/Deprecated.cs.
#    - Untracked file: scratch/notes.txt.
#
#  Check:
#  - R2 (unified / side-by-side, prev/next, minimap, inline spans).
#  - R9 + B12 (stage and unstage selected lines, then verify with git diff --cached).
#  - R1/R3 (theme switch across lang/).
#  - Manual checks 8, 9.
# @template: _base

source "$SCENARIO_LIB"
lab_begin

lab_use_template _base
lab_pin_config "$SCENARIO_LOCAL1"
lab_pin_config "$SCENARIO_LOCAL2"

# ── 1. Create baseline files in Local1 ──
mkdir -p "$SCENARIO_LOCAL1/src/Orders" \
         "$SCENARIO_LOCAL1/src/Tokenizer" \
         "$SCENARIO_LOCAL1/src/Legacy" \
         "$SCENARIO_LOCAL1/docs" \
         "$SCENARIO_LOCAL1/lang" \
         "$SCENARIO_LOCAL1/scratch"

# OrderService.cs baseline (~300 lines)
awk 'BEGIN {
    for (i = 1; i <= 300; i++) {
        printf("line %03d: public void ProcessOrderStep%03d() { /* baseline step */ }\n", i, i);
    }
}' > "$SCENARIO_LOCAL1/src/Orders/OrderService.cs"

# OrderValidator.cs baseline (~100 lines)
awk 'BEGIN {
    for (i = 1; i <= 100; i++) {
        printf("line %03d: public bool ValidateRule%03d() { return true; }\n", i, i);
    }
}' > "$SCENARIO_LOCAL1/src/Orders/OrderValidator.cs"

# Tokenizer/Comments.cs baseline
cat << 'EOF' > "$SCENARIO_LOCAL1/src/Tokenizer/Comments.cs"
// Comments tokenizer test
/* start multiline comment block
   intermediate block line 1
   intermediate block line 2
   end multiline comment block */

public class TokenizerTest
{
    public string Description = "valid double-quoted string";
    public string UnmatchedString = "initial balanced quote";
}
EOF

# docs/long-lines.md baseline with line > 300 chars
cat << 'EOF' > "$SCENARIO_LOCAL1/docs/long-lines.md"
# Documentation

Short introductory paragraph.

Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non proident.

Closing remarks here.
EOF

# Legacy/Old.cs baseline for rename
cat << 'EOF' > "$SCENARIO_LOCAL1/src/Legacy/Old.cs"
public class LegacyService
{
    public void ExecuteOldAction()
    {
        // step 1
        // step 2
        // step 3
    }
}
EOF

# Deprecated.cs baseline for unstaged deletion
echo "public class DeprecatedService { }" > "$SCENARIO_LOCAL1/src/Deprecated.cs"

# Multi-language files in lang/ (20+ lines each)
for ext in py ts json xml axaml md toml kt h yml sh unknownext; do
    awk 'BEGIN {
        for (i = 1; i <= 30; i++) {
            printf("entry_%02d: content for item %02d\n", i, i);
        }
    }' > "$SCENARIO_LOCAL1/lang/sample.$ext"
done

lab_commit "$SCENARIO_LOCAL1" "chore: add diff staging playground baseline files"

# ── 2. Produce 12 unstaged hunks in OrderService.cs ──
awk '
{
    n = NR
    # Hunk 1 (line 20): one-word edit
    if (n == 20) {
        print "line 020: public void ProcessOrderStep020_MODIFIED() { /* baseline step */ }"
        next
    }
    # Hunk 2 (line 45-47): replacement block (3 - -> 4 +)
    if (n == 45) {
        print "line 045: replacement block row 1"
        print "line 045: replacement block row 2"
        print "line 045: replacement block row 3"
        print "line 045: replacement block row 4"
        next
    }
    if (n == 46 || n == 47) { next }

    # Hunk 3 (line 70): pure addition
    if (n == 70) {
        print $0
        print "line 070: pure addition line A"
        print "line 070: pure addition line B"
        next
    }
    # Hunk 4 (lines 95-96): pure deletion
    if (n == 95 || n == 96) { next }

    # Hunk 5 (line 120): one-word edit
    if (n == 120) {
        print "line 120: public void ProcessOrderStep120_MODIFIED() { /* baseline step */ }"
        next
    }
    # Hunk 6 (lines 145-147): replacement block (3 - -> 4 +)
    if (n == 145) {
        print "line 145: replacement block row 1"
        print "line 145: replacement block row 2"
        print "line 145: replacement block row 3"
        print "line 145: replacement block row 4"
        next
    }
    if (n == 146 || n == 147) { next }

    # Hunk 7 (line 170): pure addition
    if (n == 170) {
        print $0
        print "line 170: pure addition line single"
        next
    }
    # Hunk 8 (lines 195-196): pure deletion
    if (n == 195 || n == 196) { next }

    # Hunk 9 (line 220): one-word edit
    if (n == 220) {
        print "line 220: public void ProcessOrderStep220_MODIFIED() { /* baseline step */ }"
        next
    }
    # Hunk 10 (lines 245-247): replacement block (3 - -> 4 +)
    if (n == 245) {
        print "line 245: replacement block row 1"
        print "line 245: replacement block row 2"
        print "line 245: replacement block row 3"
        print "line 245: replacement block row 4"
        next
    }
    if (n == 246 || n == 247) { next }

    # Hunk 11 (line 270): pure addition
    if (n == 270) {
        print $0
        print "line 270: pure addition line final"
        next
    }
    # Hunk 12 (line 295): pure deletion
    if (n == 295) { next }

    print $0
}
' "$SCENARIO_LOCAL1/src/Orders/OrderService.cs" > "$SCENARIO_LOCAL1/src/Orders/OrderService.cs.tmp"
mv "$SCENARIO_LOCAL1/src/Orders/OrderService.cs.tmp" "$SCENARIO_LOCAL1/src/Orders/OrderService.cs"

# ── 3. Partial staging in OrderValidator.cs (MM) ──
# Stage hunks 1 & 2 (hunk 1 is mixed add/delete replacement)
awk '
{
    n = NR
    if (n == 15) {
        print "line 015: replaced validation rule row 1"
        print "line 015: replaced validation rule row 2"
        next
    }
    if (n == 16 || n == 17) { next }
    if (n == 35) {
        print "line 035: public bool ValidateRule035_STAGED() { return true; }"
        next
    }
    print $0
}
' "$SCENARIO_LOCAL1/src/Orders/OrderValidator.cs" > "$SCENARIO_LOCAL1/src/Orders/OrderValidator.cs.tmp"
mv "$SCENARIO_LOCAL1/src/Orders/OrderValidator.cs.tmp" "$SCENARIO_LOCAL1/src/Orders/OrderValidator.cs"
git -C "$SCENARIO_LOCAL1" add src/Orders/OrderValidator.cs

# Unstaged hunks 3 & 4
awk '
{
    n = NR
    if (n == 60) {
        print $0
        print "line 060: unstaged rule addition"
        next
    }
    if (n == 85) {
        print "line 085: public bool ValidateRule085_UNSTAGED() { return false; }"
        next
    }
    print $0
}
' "$SCENARIO_LOCAL1/src/Orders/OrderValidator.cs" > "$SCENARIO_LOCAL1/src/Orders/OrderValidator.cs.tmp"
mv "$SCENARIO_LOCAL1/src/Orders/OrderValidator.cs.tmp" "$SCENARIO_LOCAL1/src/Orders/OrderValidator.cs"

# ── 4. Tokenizer/Comments.cs edits ──
# Delete line 2 opening /* and leave unmatched " on UnmatchedString
sed -i '2d' "$SCENARIO_LOCAL1/src/Tokenizer/Comments.cs"
sed -i 's/initial balanced quote/unmatched string quote/' "$SCENARIO_LOCAL1/src/Tokenizer/Comments.cs"

# ── 5. docs/long-lines.md prose edit ──
sed -i 's/quis nostrud exercitation/quis NOSTRUD EXERCITATION/' "$SCENARIO_LOCAL1/docs/long-lines.md"

# ── 6. Staged rename with edit ──
mkdir -p "$SCENARIO_LOCAL1/src/Core"
git -C "$SCENARIO_LOCAL1" mv src/Legacy/Old.cs src/Core/New.cs
sed -i 's/ExecuteOldAction/ExecuteRenamedAction/' "$SCENARIO_LOCAL1/src/Core/New.cs"
git -C "$SCENARIO_LOCAL1" add src/Core/New.cs

# ── 7. Staged new file ──
echo "public class NewFeature { }" > "$SCENARIO_LOCAL1/src/NewFeature.cs"
git -C "$SCENARIO_LOCAL1" add src/NewFeature.cs

# ── 8. Unstaged deletion ──
rm "$SCENARIO_LOCAL1/src/Deprecated.cs"

# ── 9. Untracked file ──
echo "untracked scratchpad notes" > "$SCENARIO_LOCAL1/scratch/notes.txt"

# ── 10. Multi-language 2-hunk unstaged edits ──
for ext in py ts json xml axaml md toml kt h yml sh unknownext; do
    sed -i -e '5s/.*/# edit 1 modified entry/' -e '25s/.*/# edit 2 modified entry/' "$SCENARIO_LOCAL1/lang/sample.$ext"
done

# ── Self-checks ──
lab_assert_op "$SCENARIO_LOCAL1" none
lab_assert "OrderService.cs has exactly 12 hunks" [ "$(git -C "$SCENARIO_LOCAL1" diff -U3 -- src/Orders/OrderService.cs | grep -c '^@@')" -eq 12 ]
lab_assert_status "$SCENARIO_LOCAL1" src/Orders/OrderValidator.cs "MM"
lab_assert "Staged rename detected" [ -n "$(git -C "$SCENARIO_LOCAL1" diff --cached --name-status | grep '^R')" ]
lab_assert "Staged new file detected" [ -n "$(git -C "$SCENARIO_LOCAL1" diff --cached --name-status | grep '^A')" ]
lab_assert "Unstaged deletion detected" [ -n "$(git -C "$SCENARIO_LOCAL1" status --porcelain=v1 | grep '^ D src/Deprecated.cs')" ]
lab_assert "Untracked file detected" [ -n "$(git -C "$SCENARIO_LOCAL1" status --porcelain=v1 | grep '^\?\?')" ]

lab_done
