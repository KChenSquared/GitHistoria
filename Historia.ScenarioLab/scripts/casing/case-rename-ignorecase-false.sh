#!/usr/bin/env bash
# @short: Local1 renamed Foo.txt to foo.txt on disk with core.ignorecase=false

source "$SCENARIO_LIB"
lab_begin

lab_use_template _base
lab_pin_config "$SCENARIO_LOCAL1"

git -C "$SCENARIO_LOCAL1" config core.ignorecase false
git -C "$SCENARIO_LOCAL1" config core.fscache true

mkdir -p "$SCENARIO_LOCAL1/Src"
echo "hello" > "$SCENARIO_LOCAL1/Foo.txt"
echo "source" > "$SCENARIO_LOCAL1/Src/a.cs"
git -C "$SCENARIO_LOCAL1" add Foo.txt Src/a.cs
lab_commit "$SCENARIO_LOCAL1" "Add Foo.txt and Src/a.cs"

# Rename on disk via temporary names so NTFS allows the case-only rename
(cd "$SCENARIO_LOCAL1" && mv Foo.txt t && mv t foo.txt)
(cd "$SCENARIO_LOCAL1" && mv Src temp_src && mv temp_src src)

STATUS_V1="$(git -C "$SCENARIO_LOCAL1" status --porcelain=v1)"
STATUS_V2="$(git -C "$SCENARIO_LOCAL1" status --porcelain=v2)"

# Assert that status shows .D Foo.txt and ? foo.txt
lab_assert "Status shows deleted Foo.txt" [ -n "$(printf '%s\n' "$STATUS_V1" | grep ' D Foo.txt')" ]
lab_assert "Status shows untracked foo.txt" [ -n "$(printf '%s\n' "$STATUS_V1" | grep '?? foo.txt')" ]
lab_assert "Status v2 shows .D Foo.txt" [ -n "$(printf '%s\n' "$STATUS_V2" | grep 'Foo.txt' | grep '1 \.D')" ]
lab_assert "Status v2 shows ? foo.txt" [ -n "$(printf '%s\n' "$STATUS_V2" | grep '\? foo.txt')" ]

lab_done
