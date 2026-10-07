#!/usr/bin/env bash
# @short: Local1 in stash-pop conflict; Local2 in checkout -m conflict
# @long:
#  State:
#  - Local1: mid-conflict from 'git stash pop'. Working tree has UU on src/config.cs,
#    stash entry is preserved in git stash list, and no operation is in progress.
#  - Local2: mid-conflict from 'git checkout -m other'. Working tree has UU on
#    src/editor.cs, current branch is 'other', and no operation is in progress.
#
#  Check:
#  - B8 (conflict resolver appears with no Continue / Skip / Abort operation buttons).
#  - R6 flows on a non-operation conflict.
#  - Manual check 10.

source "$SCENARIO_LIB"
lab_begin

lab_use_template _base

lab_pin_config "$SCENARIO_LOCAL1"
lab_pin_config "$SCENARIO_LOCAL2"

# ── 1. Local1: Setup stash pop conflict ──
mkdir -p "$SCENARIO_LOCAL1/src"
cat << 'EOF' > "$SCENARIO_LOCAL1/src/config.cs"
public class ServiceConfig
{
    public string ServerUrl = "base-endpoint";
}
EOF

cat << 'EOF' > "$SCENARIO_LOCAL1/src/other.cs"
public class OtherService
{
    public int RetryCount = 3;
}
EOF

lab_commit "$SCENARIO_LOCAL1" "Initial configuration files"
git -C "$SCENARIO_LOCAL1" push origin main

# Make working edits to both files and stash
cat << 'EOF' > "$SCENARIO_LOCAL1/src/config.cs"
public class ServiceConfig
{
    public string ServerUrl = "stashed-endpoint";
}
EOF
echo "// Clean edit in other" >> "$SCENARIO_LOCAL1/src/other.cs"
git -C "$SCENARIO_LOCAL1" stash push -m "WIP local configuration"

# Commit conflicting change on main
cat << 'EOF' > "$SCENARIO_LOCAL1/src/config.cs"
public class ServiceConfig
{
    public string ServerUrl = "upstream-endpoint";
}
EOF
lab_commit "$SCENARIO_LOCAL1" "Main: update ServerUrl upstream"

# Pop stash (conflicts on config.cs, preserves stash entry)
lab_expect_conflict "$SCENARIO_LOCAL1" "stash pop conflict" stash pop

# ── 2. Local2: Setup checkout -m conflict ──
mkdir -p "$SCENARIO_LOCAL2/src"
cat << 'EOF' > "$SCENARIO_LOCAL2/src/editor.cs"
public class EditorOptions
{
    public string Theme = "Default";
}
EOF
lab_commit "$SCENARIO_LOCAL2" "Initial editor options"

# Branch 'other' and commit modified theme
git -C "$SCENARIO_LOCAL2" checkout -b other main
cat << 'EOF' > "$SCENARIO_LOCAL2/src/editor.cs"
public class EditorOptions
{
    public string Theme = "Dark";
}
EOF
lab_commit "$SCENARIO_LOCAL2" "Other branch: set Dark theme"

# Switch back to main, make conflicting dirty edit in working tree
git -C "$SCENARIO_LOCAL2" checkout main
cat << 'EOF' > "$SCENARIO_LOCAL2/src/editor.cs"
public class EditorOptions
{
    public string Theme = "Light";
}
EOF

# Run checkout -m other (merges dirty working changes with target branch; conflicts)
lab_expect_conflict "$SCENARIO_LOCAL2" "checkout -m other conflict" checkout -m other

# ── 3. Self-checks ──
# Local1: operation is none, config.cs is UU, stash entry is kept
lab_assert_op "$SCENARIO_LOCAL1" none
lab_assert_status "$SCENARIO_LOCAL1" src/config.cs UU
lab_assert "Local1 stash entry is kept" [ "$(git -C "$SCENARIO_LOCAL1" stash list | wc -l)" -eq 1 ]

# Local2: operation is none, editor.cs is UU, branch is other
lab_assert_op "$SCENARIO_LOCAL2" none
lab_assert_status "$SCENARIO_LOCAL2" src/editor.cs UU
lab_assert "Local2 is on branch other" [ "$(git -C "$SCENARIO_LOCAL2" branch --show-current)" = "other" ]

lab_done
