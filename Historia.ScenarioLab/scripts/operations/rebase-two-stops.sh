#!/usr/bin/env bash
# @short: Local1 stopped at C1 rebase conflict; Local2 has C2 edit stop
# @long:
#  State:
#  - Local1: mid-rebase on feature/rate-limit onto origin/main, stopped at C1 conflict.
#  - Local2: mid-interactive-rebase on feature/rate-limit onto origin/main, stopped at C1
#    conflict, with C2 marked as 'edit' in rebase-merge/git-rebase-todo.
#
#  Check:
#  - B4 (rebase conflict labels: ours = origin/main, the upstream branch).
#  - B6 (Skip button shown during rebase conflict).
#  - B11 (Continue in Local1 moves to C3 in the resolver; in Local2, continuing after
#    resolving lands on C2 'edit' stop with no conflicts, showing op banner).
#  - Manual checks 3, 10.

source "$SCENARIO_LIB"
lab_begin

lab_use_template _base

lab_pin_config "$SCENARIO_LOCAL1"
lab_pin_config "$SCENARIO_LOCAL2"

# ── 1. Create base files on main ──
mkdir -p "$SCENARIO_LOCAL1/src"
cat << 'EOF' > "$SCENARIO_LOCAL1/src/rate-limit.cs"
public class RateLimiter
{
    public const int MaxRequests = 10;
}
EOF

cat << 'EOF' > "$SCENARIO_LOCAL1/src/config.cs"
public class AppConfig
{
    public const int TimeoutSeconds = 15;
}
EOF

lab_commit "$SCENARIO_LOCAL1" "Initial service setup"
git -C "$SCENARIO_LOCAL1" push origin main

# ── 2. Create feature/rate-limit with C1 (conflict), C2 (clean), C3 (conflict) ──
git -C "$SCENARIO_LOCAL1" checkout -b feature/rate-limit main

# C1: conflicting rate limit change
cat << 'EOF' > "$SCENARIO_LOCAL1/src/rate-limit.cs"
public class RateLimiter
{
    public const int MaxRequests = 100;
}
EOF
lab_commit "$SCENARIO_LOCAL1" "C1: rate limit to 100"
C1_SHA="$(git -C "$SCENARIO_LOCAL1" rev-parse HEAD)"

# C2: clean config change (will be marked 'edit' in Local2)
cat << 'EOF' > "$SCENARIO_LOCAL1/src/config.cs"
public class AppConfig
{
    public const int TimeoutSeconds = 60;
}
EOF
lab_commit "$SCENARIO_LOCAL1" "C2: timeout to 60"
C2_SHA="$(git -C "$SCENARIO_LOCAL1" rev-parse HEAD)"

# C3: further rate limit change
cat << 'EOF' > "$SCENARIO_LOCAL1/src/rate-limit.cs"
public class RateLimiter
{
    public const int MaxRequests = 200;
}
EOF
lab_commit "$SCENARIO_LOCAL1" "C3: rate limit to 200"

git -C "$SCENARIO_LOCAL1" push -u origin feature/rate-limit

# ── 3. Advance main on remote with conflicting edit ──
git -C "$SCENARIO_LOCAL1" checkout main
cat << 'EOF' > "$SCENARIO_LOCAL1/src/rate-limit.cs"
public class RateLimiter
{
    public const int MaxRequests = 500;
}
EOF
lab_commit "$SCENARIO_LOCAL1" "Main: rate limit upstream to 500"
git -C "$SCENARIO_LOCAL1" push origin main

# ── 4. Local1: rebase feature/rate-limit onto origin/main (stopped at C1) ──
git -C "$SCENARIO_LOCAL1" checkout feature/rate-limit
git -C "$SCENARIO_LOCAL1" fetch origin
lab_expect_conflict "$SCENARIO_LOCAL1" "rebase onto origin/main" rebase origin/main

# ── 5. Local2: interactive rebase setting C2 to edit (stopped at C1) ──
git -C "$SCENARIO_LOCAL2" fetch origin
git -C "$SCENARIO_LOCAL2" checkout -b feature/rate-limit origin/feature/rate-limit
GIT_SEQUENCE_EDITOR="sed -i -e '2s/^pick/edit/'" lab_expect_conflict "$SCENARIO_LOCAL2" "interactive rebase with edit stop" rebase -i origin/main

# ── 6. Self-checks ──
lab_assert_op "$SCENARIO_LOCAL1" rebase
lab_assert_op "$SCENARIO_LOCAL2" rebase

lab_assert_status "$SCENARIO_LOCAL1" src/rate-limit.cs UU
lab_assert_status "$SCENARIO_LOCAL2" src/rate-limit.cs UU

# Local1 msgnum is 1 and REBASE_HEAD matches C1
GDIR_L1="$(_lab_resolve_git_dir "$SCENARIO_LOCAL1")"
lab_assert "Local1 msgnum is 1" [ "$(cat "$GDIR_L1/rebase-merge/msgnum")" = "1" ]
L1_REBASE_HEAD="$(cat "$GDIR_L1/REBASE_HEAD" 2>/dev/null || cat "$GDIR_L1/rebase-merge/stopped-sha")"
lab_assert "Local1 REBASE_HEAD is C1" [ "$L1_REBASE_HEAD" = "$C1_SHA" ]

# Local2 msgnum is 1 and todo begins with edit for C2
GDIR_L2="$(_lab_resolve_git_dir "$SCENARIO_LOCAL2")"
lab_assert "Local2 msgnum is 1" [ "$(cat "$GDIR_L2/rebase-merge/msgnum")" = "1" ]
L2_REBASE_HEAD="$(cat "$GDIR_L2/REBASE_HEAD" 2>/dev/null || cat "$GDIR_L2/rebase-merge/stopped-sha")"
lab_assert "Local2 REBASE_HEAD is C1" [ "$L2_REBASE_HEAD" = "$C1_SHA" ]

lab_assert "Local2 todo starts with edit for C2" grep -q '^edit ' "$GDIR_L2/rebase-merge/git-rebase-todo"

lab_done
