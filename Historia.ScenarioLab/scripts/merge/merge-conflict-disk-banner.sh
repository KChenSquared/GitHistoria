#!/usr/bin/env bash
# @short: 3-file merge conflict; Local2 holds files to edit Local1 on disk
# @long:
#  Local1 has fetched origin/main but not merged it: merging conflicts in
#  config/app.ini, src/greeting.py and docs/guide.md (one hunk each).
#
#  Local2 contains an untracked external-edits/ folder whose layout mirrors
#  the repo root. Use it to test the conflict resolver's "changed on disk"
#  banner:
#
#  1. Open Local1 in Historia and merge origin/main to get the conflicts.
#  2. In the resolver, edit the Result pane of one file without saving.
#  3. Copy the matching file from Local2/external-edits/ over the same path
#  in Local1, then refocus Historia. The banner should appear.
#  4. Switch to another file (Save or Discard when prompted). The banner
#  should be gone on the new file.
#  5. Repeat 2-3, then resolve every file. The all-resolved card should not
#  show the banner.

source "$SCENARIO_LIB"
lab_begin

lab_use_template _base

# Writes the three conflict candidates into <dir>, with one differing line each.
#   $1 dir   $2 port   $3 greeting prefix   $4 guide status
write_files() {
    local dir="$1" port="$2" prefix="$3" status="$4"
    mkdir -p "$dir/config" "$dir/src" "$dir/docs"

    cat > "$dir/config/app.ini" << EOF
; Application Configuration File
[server]
host = 127.0.0.1
port = $port
timeout = 30
max_connections = 100

[logging]
level = INFO
EOF

    cat > "$dir/src/greeting.py" << EOF
"""Greeting service module."""

import sys

GREETING_PREFIX = "$prefix"
DEFAULT_TITLE = "Guest"

def get_greeting(name=None):
    return f"{GREETING_PREFIX}, {name or DEFAULT_TITLE}!"

if __name__ == "__main__":
    print(get_greeting(sys.argv[1] if len(sys.argv) > 1 else None))
EOF

    cat > "$dir/docs/guide.md" << EOF
# Application User Guide

## Introduction
The service provides reliable message formatting.
Status: $status
Maintainer: ScenarioLab Core Team

## Support
Open a GitHub issue for bugs or feature requests.
EOF
}

files_differ() { ! cmp -s "$1" "$2"; }

# ── Step 1: Local1 creates the initial files and pushes to mainRemote ──
write_files "$SCENARIO_LOCAL1" 8080 "Hello" "Production Ready v1.0"
lab_commit "$SCENARIO_LOCAL1" "Add application configuration, greeting service, and guide"
git -C "$SCENARIO_LOCAL1" push origin main

# ── Step 2: Local2 pulls, edits each file, and pushes to mainRemote ──
git -C "$SCENARIO_LOCAL2" pull
write_files "$SCENARIO_LOCAL2" 9000 "Welcome" "Staging Preview v1.1"
lab_commit "$SCENARIO_LOCAL2" "Local2 updates config, greeting, and guide"
git -C "$SCENARIO_LOCAL2" push origin main

# ── Step 3: Local1 edits the same lines differently, commits, and fetches ──
write_files "$SCENARIO_LOCAL1" 8443 "Greetings" "Release Candidate v1.0-RC1"
lab_commit "$SCENARIO_LOCAL1" "Local1 updates config, greeting, and guide"
git -C "$SCENARIO_LOCAL1" fetch origin

# ── Step 4: Local2 gets untracked "external edit" copies to drop into Local1 ──
write_files "$SCENARIO_LOCAL2/external-edits" 7000 "Howdy" "Edited outside Historia"

# ── Self-check ──
AHEAD_BEHIND="$(git -C "$SCENARIO_LOCAL1" rev-list --left-right --count main...origin/main)"
lab_assert "Local1 is 1 ahead and 1 behind origin/main" [ "$AHEAD_BEHIND" = "1	1" ]

set +e
MT_OUT="$(git -C "$SCENARIO_LOCAL1" merge-tree --write-tree --name-only --no-messages main origin/main)"
MT_STATUS=$?
set -e

lab_assert "merge-tree exited with status 1 (conflict detected)" [ "$MT_STATUS" -eq 1 ]

TREE="$(echo "$MT_OUT" | head -n 1)"
CONFLICTED_FILES="$(echo "$MT_OUT" | tail -n +2 | sort | tr '\n' ' ' | xargs)"
lab_assert "Conflicted files match expected 3 files" [ "$CONFLICTED_FILES" = "config/app.ini docs/guide.md src/greeting.py" ]

for f in config/app.ini src/greeting.py docs/guide.md; do
    HUNKS="$(git -C "$SCENARIO_LOCAL1" cat-file -p "$TREE:$f" | grep -c '^<<<<<<< ' || true)"
    lab_assert "$f has exactly 1 conflict hunk" [ "$HUNKS" -eq 1 ]
    lab_assert "Local2 has external-edits/$f" [ -f "$SCENARIO_LOCAL2/external-edits/$f" ]
    lab_assert "external-edits/$f differs from Local1's $f" files_differ "$SCENARIO_LOCAL2/external-edits/$f" "$SCENARIO_LOCAL1/$f"
done

lab_assert "external-edits/ is untracked in Local2" \
    grep -q '^?? external-edits/' <(git -C "$SCENARIO_LOCAL2" status --porcelain)

lab_done

cat << EOF

── Manual steps: changed-on-disk banner ──
1. Open Local1 in Historia and merge origin/main to get the 3 conflicts.
2. In the conflict resolver, edit the Result pane of one file without saving.
3. Copy the matching file from
     $SCENARIO_LOCAL2/external-edits/<path>
   over
     $SCENARIO_LOCAL1/<path>
   then refocus Historia. The "changed on disk" banner should appear.
   (Copy everything at once: cp -r "$SCENARIO_LOCAL2/external-edits/." "$SCENARIO_LOCAL1/")
4. Switch to another file (Save or Discard when prompted): no banner on the new file.
5. Repeat 2-3, then resolve every file: the all-resolved card shows no banner.
EOF
