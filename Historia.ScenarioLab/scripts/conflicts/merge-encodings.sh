#!/usr/bin/env bash
# @short: Mid-merge on varied encodings (UTF-8 BOM, UTF-16, Latin-1, CRLF)
# @long:
#  State:
#  - Local1: mid-merge with core.autocrlf=false pinned. Files: bom/Utf8Bom.cs
#    (UTF-8 BOM, region on line 1), bom/Utf8BomMid.cs (BOM, region mid-file),
#    enc/utf16le.txt (UTF-16LE with BOM, CRLF), enc/latin1.txt (raw byte 0xE9),
#    eol/crlf-index.txt (-text, CRLF in index), eol/mixed.txt (-text, mixed endings),
#    eol/unix.sh (LF endings).
#  - Local2: mid-merge with core.autocrlf=true pinned on the same conflicts.
#
#  Check:
#  - B2 / B3 (Take theirs and Save keep BOM, EOL, UTF-16; compare Local1 vs Local2).
#  - B7 (external tool temp file named LOCAL.cs etc.).
#  - Non-UTF-8 (Latin-1) triggers file-level actions only.
#  - Manual check 2.

source "$SCENARIO_LIB"
lab_begin

lab_use_template _base

# Pin core.autocrlf false early so initial commits are not affected by system autocrlf
lab_pin_config "$SCENARIO_LOCAL1" core.autocrlf false
lab_pin_config "$SCENARIO_LOCAL2" core.autocrlf false

# ── 1. Create .gitattributes and base files on main ──
mkdir -p "$SCENARIO_LOCAL1/bom" "$SCENARIO_LOCAL1/enc" "$SCENARIO_LOCAL1/eol"

cat << 'EOF' > "$SCENARIO_LOCAL1/.gitattributes"
enc/utf16le.txt text working-tree-encoding=UTF-16LE-BOM eol=crlf
eol/crlf-index.txt -text
eol/mixed.txt -text
eol/unix.sh text eol=lf
EOF

# bom/Utf8Bom.cs (BOM + region on line 1)
printf '\xef\xbb\xbfpublic class Utf8Bom\n{\n    public int Value = 1;\n}\n' > "$SCENARIO_LOCAL1/bom/Utf8Bom.cs"

# bom/Utf8BomMid.cs (BOM + region mid-file)
printf '\xef\xbb\xbf// Class header\npublic class Utf8BomMid\n{\n    public string Mode = "Base";\n}\n' > "$SCENARIO_LOCAL1/bom/Utf8BomMid.cs"

# enc/utf16le.txt (UTF-16LE BOM)
printf 'Title: Base UTF-16\r\nValue: 100\r\n' | iconv -f UTF-8 -t UTF-16LE > "$SCENARIO_LOCAL1/enc/utf16le.txt"

# enc/latin1.txt (Latin-1 with e-acute 0xE9)
printf 'Caf\xe9 base menu\nPrice: 10\n' > "$SCENARIO_LOCAL1/enc/latin1.txt"

# eol/crlf-index.txt (CRLF committed under -text)
printf 'line 1\r\nline 2 base\r\nline 3\r\n' > "$SCENARIO_LOCAL1/eol/crlf-index.txt"

# eol/mixed.txt (mixed CRLF and LF lines under -text)
printf 'line 1 lf\nline 2 crlf\r\nline 3 base\n' > "$SCENARIO_LOCAL1/eol/mixed.txt"

# eol/unix.sh (LF)
printf '#!/bin/sh\necho "base unix"\n' > "$SCENARIO_LOCAL1/eol/unix.sh"

lab_commit "$SCENARIO_LOCAL1" "Initial commit: encoding and EOL test files"
git -C "$SCENARIO_LOCAL1" push origin main

# ── 2. Local2 branches release/encodings, edits, and pushes ──
git -C "$SCENARIO_LOCAL2" fetch origin
git -C "$SCENARIO_LOCAL2" checkout -b release/encodings origin/main

printf '\xef\xbb\xbfpublic class Utf8BomTheirs\n{\n    public int Value = 3;\n}\n' > "$SCENARIO_LOCAL2/bom/Utf8Bom.cs"
printf '\xef\xbb\xbf// Class header\npublic class Utf8BomMid\n{\n    public string Mode = "Theirs";\n}\n' > "$SCENARIO_LOCAL2/bom/Utf8BomMid.cs"
printf 'Title: Base UTF-16\r\nValue: 300\r\n' | iconv -f UTF-8 -t UTF-16LE > "$SCENARIO_LOCAL2/enc/utf16le.txt"
printf 'Caf\xe9 theirs menu\nPrice: 30\n' > "$SCENARIO_LOCAL2/enc/latin1.txt"
printf 'line 1\r\nline 2 theirs\r\nline 3\r\n' > "$SCENARIO_LOCAL2/eol/crlf-index.txt"
printf 'line 1 lf\nline 2 crlf\r\nline 3 theirs\n' > "$SCENARIO_LOCAL2/eol/mixed.txt"
printf '#!/bin/sh\necho "theirs unix"\n' > "$SCENARIO_LOCAL2/eol/unix.sh"

lab_commit "$SCENARIO_LOCAL2" "Release edits for encodings"
git -C "$SCENARIO_LOCAL2" push -u origin release/encodings

# ── 3. Local1 branches feature/encodings, edits, pushes, fetches ──
git -C "$SCENARIO_LOCAL1" checkout -b feature/encodings main

printf '\xef\xbb\xbfpublic class Utf8BomOurs\n{\n    public int Value = 2;\n}\n' > "$SCENARIO_LOCAL1/bom/Utf8Bom.cs"
printf '\xef\xbb\xbf// Class header\npublic class Utf8BomMid\n{\n    public string Mode = "Ours";\n}\n' > "$SCENARIO_LOCAL1/bom/Utf8BomMid.cs"
printf 'Title: Base UTF-16\r\nValue: 200\r\n' | iconv -f UTF-8 -t UTF-16LE > "$SCENARIO_LOCAL1/enc/utf16le.txt"
printf 'Caf\xe9 ours menu\nPrice: 20\n' > "$SCENARIO_LOCAL1/enc/latin1.txt"
printf 'line 1\r\nline 2 ours\r\nline 3\r\n' > "$SCENARIO_LOCAL1/eol/crlf-index.txt"
printf 'line 1 lf\nline 2 crlf\r\nline 3 ours\n' > "$SCENARIO_LOCAL1/eol/mixed.txt"
printf '#!/bin/sh\necho "ours unix"\n' > "$SCENARIO_LOCAL1/eol/unix.sh"

lab_commit "$SCENARIO_LOCAL1" "Feature edits for encodings"
git -C "$SCENARIO_LOCAL1" push -u origin feature/encodings
git -C "$SCENARIO_LOCAL1" fetch origin

# ── 4. Local1 runs merge (core.autocrlf=false pinned) ──
lab_pin_config "$SCENARIO_LOCAL1" core.autocrlf false
lab_expect_conflict "$SCENARIO_LOCAL1" "merge release/encodings with autocrlf false" merge origin/release/encodings

# ── 5. Local2 checks out feature/encodings and runs merge (core.autocrlf=true pinned) ──
git -C "$SCENARIO_LOCAL2" fetch origin
git -C "$SCENARIO_LOCAL2" checkout -b feature/encodings origin/feature/encodings
lab_pin_config "$SCENARIO_LOCAL2" core.autocrlf true
lab_expect_conflict "$SCENARIO_LOCAL2" "merge release/encodings with autocrlf true" merge origin/release/encodings

# ── 6. Self-checks ──
lab_assert_op "$SCENARIO_LOCAL1" merge
lab_assert_op "$SCENARIO_LOCAL2" merge

# All files conflicted (UU)
lab_assert_status "$SCENARIO_LOCAL1" bom/Utf8Bom.cs UU
lab_assert_status "$SCENARIO_LOCAL1" bom/Utf8BomMid.cs UU
lab_assert_status "$SCENARIO_LOCAL1" enc/utf16le.txt UU
lab_assert_status "$SCENARIO_LOCAL1" enc/latin1.txt UU
lab_assert_status "$SCENARIO_LOCAL1" eol/crlf-index.txt UU
lab_assert_status "$SCENARIO_LOCAL1" eol/mixed.txt UU
lab_assert_status "$SCENARIO_LOCAL1" eol/unix.sh UU

# BOM checks: Utf8BomMid.cs has BOM at byte 0; Utf8Bom.cs line 1 is start marker and line 2 (ours hunk) has BOM
lab_assert "Local1 Utf8BomMid.cs has UTF-8 BOM at byte 0" [ "$(head -c 3 "$SCENARIO_LOCAL1/bom/Utf8BomMid.cs")" = "$(printf '\xef\xbb\xbf')" ]
lab_assert "Local1 Utf8Bom.cs line 1 has start marker" [ "$(head -n 1 "$SCENARIO_LOCAL1/bom/Utf8Bom.cs" | tr -d '\r' | cut -c 1-7)" = "<<<<<<<" ]
lab_assert "Local1 Utf8Bom.cs line 2 has UTF-8 BOM" [ "$(sed -n '2p' "$SCENARIO_LOCAL1/bom/Utf8Bom.cs" | head -c 3)" = "$(printf '\xef\xbb\xbf')" ]
lab_assert "Local1 Utf8Bom.cs stage 2 has UTF-8 BOM at byte 0" [ "$(git -C "$SCENARIO_LOCAL1" show :2:bom/Utf8Bom.cs | head -c 3)" = "$(printf '\xef\xbb\xbf')" ]
lab_assert "Local1 Utf8Bom.cs stage 3 has UTF-8 BOM at byte 0" [ "$(git -C "$SCENARIO_LOCAL1" show :3:bom/Utf8Bom.cs | head -c 3)" = "$(printf '\xef\xbb\xbf')" ]

# enc/utf16le.txt starts FF FE and has 1 region via lab_count_markers
lab_assert "utf16le.txt starts with UTF-16LE BOM" [ "$(head -c 2 "$SCENARIO_LOCAL1/enc/utf16le.txt")" = "$(printf '\xff\xfe')" ]
lab_assert "utf16le.txt has 1 conflict region" [ "$(lab_count_markers "$SCENARIO_LOCAL1/enc/utf16le.txt" '<' 7 UTF-16LE)" -eq 1 ]

# latin1.txt contains byte 0xE9
lab_assert "latin1.txt contains byte 0xE9" grep -q -U "$(printf '\xe9')" "$SCENARIO_LOCAL1/enc/latin1.txt"

# crlf-index.txt shows i/crlf in git ls-files --eol
lab_assert "crlf-index.txt has i/crlf" grep -q "i/crlf" <(git -C "$SCENARIO_LOCAL1" ls-files --eol eol/crlf-index.txt)

# Local2's Utf8BomMid.cs has CRLF working lines while Local1 has LF
CR_BYTE="$(printf '\r')"
lab_assert "Local2 Utf8BomMid.cs has CRLF line endings" grep -q -U "$CR_BYTE" "$SCENARIO_LOCAL2/bom/Utf8BomMid.cs"
lab_assert "Local1 Utf8BomMid.cs has LF line endings" [ "$(grep -c -U "$CR_BYTE" "$SCENARIO_LOCAL1/bom/Utf8BomMid.cs" || true)" -eq 0 ]

lab_done
