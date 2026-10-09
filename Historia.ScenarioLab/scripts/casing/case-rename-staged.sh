#!/usr/bin/env bash
# @short: Local1 has a staged case-only rename plus an unstaged edit

source "$SCENARIO_LIB"
lab_begin

lab_use_template _base
lab_pin_config "$SCENARIO_LOCAL1"

git -C "$SCENARIO_LOCAL1" config core.ignorecase true

echo "line 1" > "$SCENARIO_LOCAL1/Foo.txt"
git -C "$SCENARIO_LOCAL1" add Foo.txt
lab_commit "$SCENARIO_LOCAL1" "Add Foo.txt"

git -C "$SCENARIO_LOCAL1" mv Foo.txt foo.txt
echo "unstaged edit" >> "$SCENARIO_LOCAL1/foo.txt"

STATUS_V1="$(git -C "$SCENARIO_LOCAL1" status --porcelain=v1)"
STATUS_V2="$(git -C "$SCENARIO_LOCAL1" status --porcelain=v2)"

# Assert status shows RM for foo.txt with orig path Foo.txt
lab_assert "Status shows RM Foo.txt -> foo.txt" [ -n "$(printf '%s\n' "$STATUS_V1" | grep '^RM Foo.txt -> foo.txt')" ]
lab_assert "Status v2 shows RM for foo.txt with orig path Foo.txt" [ -n "$(printf '%s\n' "$STATUS_V2" | grep '^2 RM ' | grep 'Foo.txt')" ]

lab_done
