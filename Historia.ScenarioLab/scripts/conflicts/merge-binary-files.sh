#!/usr/bin/env bash
# @short: Local1 mid-merge on binary files and deleted binary (UU, UD)
# @long:
#  State:
#  - Local1: mid-merge with binary file conflicts:
#    assets/logo.png (PNG signature, changed on both sides -> UU),
#    bin/data.bin (NUL-heavy binary, changed on both -> UU),
#    assets/removed.png (ours modifies, theirs deletes -> UD),
#    docs/readme.txt (standard text conflict for contrast -> UU).
#
#  Check:
#  - B13 (Take ours / Take theirs keep the file with that side's binary bytes;
#    take theirs on removed.png removes it because stage 3 is absent).
#  - File-level conflict cards for binary assets.
#  - Manual check 2.

source "$SCENARIO_LIB"
lab_begin

lab_use_template _base

# ── 1. Create initial files on main ──
mkdir -p "$SCENARIO_LOCAL1/assets" "$SCENARIO_LOCAL1/bin" "$SCENARIO_LOCAL1/docs"

# PNG header: 0x89 0x50 0x4E 0x47 0x0D 0x0A 0x1A 0x0A
printf '\x89PNG\r\n\x1a\n\x00\x00\x00\x0dIHDRbase_bytes' > "$SCENARIO_LOCAL1/assets/logo.png"
printf '\x89PNG\r\n\x1a\n\x00\x00\x00\x0dIHDRremoved_base' > "$SCENARIO_LOCAL1/assets/removed.png"

# NUL-heavy binary data
printf 'DATA\x00\x00\x01\x00\xff\xfe\x00\x00base_payload\x00\x00' > "$SCENARIO_LOCAL1/bin/data.bin"

cat << 'EOF' > "$SCENARIO_LOCAL1/docs/readme.txt"
Project Readme
Base documentation line.
EOF

lab_commit "$SCENARIO_LOCAL1" "Initial commit: binary assets and readme"
git -C "$SCENARIO_LOCAL1" push origin main

# ── 2. Local2 branches release/binary-updates, edits, pushes ──
git -C "$SCENARIO_LOCAL2" fetch origin
git -C "$SCENARIO_LOCAL2" checkout -b release/binary-updates origin/main

printf '\x89PNG\r\n\x1a\n\x00\x00\x00\x0dIHDRtheirs_logo_bytes_v3' > "$SCENARIO_LOCAL2/assets/logo.png"
git -C "$SCENARIO_LOCAL2" rm "$SCENARIO_LOCAL2/assets/removed.png"
printf 'DATA\x00\x00\x01\x00\xff\xfe\x00\x00theirs_payload_v3\x00\x00' > "$SCENARIO_LOCAL2/bin/data.bin"

cat << 'EOF' > "$SCENARIO_LOCAL2/docs/readme.txt"
Project Readme
Theirs documentation line.
EOF

lab_commit "$SCENARIO_LOCAL2" "Release updates for binary assets"
git -C "$SCENARIO_LOCAL2" push -u origin release/binary-updates

# ── 3. Local1 branches feature/binary-rework, edits, pushes, fetches ──
git -C "$SCENARIO_LOCAL1" checkout -b feature/binary-rework main

printf '\x89PNG\r\n\x1a\n\x00\x00\x00\x0dIHDRours_logo_bytes_v2' > "$SCENARIO_LOCAL1/assets/logo.png"
printf '\x89PNG\r\n\x1a\n\x00\x00\x00\x0dIHDRremoved_ours_edit_v2' > "$SCENARIO_LOCAL1/assets/removed.png"
printf 'DATA\x00\x00\x01\x00\xff\xfe\x00\x00ours_payload_v2\x00\x00' > "$SCENARIO_LOCAL1/bin/data.bin"

cat << 'EOF' > "$SCENARIO_LOCAL1/docs/readme.txt"
Project Readme
Ours documentation line.
EOF

lab_commit "$SCENARIO_LOCAL1" "Feature rework for binary assets"
git -C "$SCENARIO_LOCAL1" push -u origin feature/binary-rework
git -C "$SCENARIO_LOCAL1" fetch origin

# ── 4. Local1 runs merge ──
lab_pin_config "$SCENARIO_LOCAL1"
lab_expect_conflict "$SCENARIO_LOCAL1" "merge release/binary-updates into feature/binary-rework" merge origin/release/binary-updates

# ── 5. Self-checks ──
lab_assert_op "$SCENARIO_LOCAL1" merge

# Status checks
lab_assert_status "$SCENARIO_LOCAL1" assets/logo.png UU
lab_assert_status "$SCENARIO_LOCAL1" bin/data.bin UU
lab_assert_status "$SCENARIO_LOCAL1" assets/removed.png UD
lab_assert_status "$SCENARIO_LOCAL1" docs/readme.txt UU

# Stages 1, 2, 3 exist for logo.png
LOGO_STAGES="$(git -C "$SCENARIO_LOCAL1" ls-files -s assets/logo.png | awk '{print $3}' | sort -u | tr '\n' ' ' | xargs)"
lab_assert "assets/logo.png has stages 1 2 3" [ "$LOGO_STAGES" = "1 2 3" ]

# Working logo.png is byte-identical to stage 2 (ours)
cmp -s "$SCENARIO_LOCAL1/assets/logo.png" <(git -C "$SCENARIO_LOCAL1" show :2:assets/logo.png)
lab_assert "working logo.png is identical to stage 2" [ $? -eq 0 ]

lab_done
