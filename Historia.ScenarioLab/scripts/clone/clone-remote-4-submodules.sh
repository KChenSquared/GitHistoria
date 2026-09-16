#!/usr/bin/env bash
# @short: Remote with 4 submodules, each with files, commits and branches
# @long:
#  Sets up mainRemote with 4 submodules in Remotes/ (Local 1 and Local 2 remain empty).
#
#  Note on local cloning: Git blocks local 'file' transport submodules by default (CVE-2022-39253).
#  Cloning this repository with submodules requires -c protocol.file.allow=always.

source "$SCENARIO_LIB"
lab_begin

# ── 1. Submodule: core-lib ──
lab_bare core-lib
CLONE_CORE="$SCENARIO_WORK/core-lib"
git clone "$SCENARIO_REMOTES/core-lib" "$CLONE_CORE"

mkdir -p "$CLONE_CORE/src" "$CLONE_CORE/tests"
cat << 'EOF' > "$CLONE_CORE/README.md"
# Core Library
Shared core utilities for application services.
EOF
cat << 'EOF' > "$CLONE_CORE/src/core.py"
def get_version():
    return "1.0.0"
EOF
lab_commit "$CLONE_CORE" "Initial commit for core-lib"

cat << 'EOF' > "$CLONE_CORE/src/types.py"
class CoreContext:
    def __init__(self, name):
        self.name = name
EOF
lab_commit "$CLONE_CORE" "Add core type definitions"

cat << 'EOF' > "$CLONE_CORE/src/utils.py"
def identity(x):
    return x
EOF
lab_commit "$CLONE_CORE" "Add utility functions"

cat << 'EOF' > "$CLONE_CORE/tests/test_core.py"
def test_version():
    assert True
EOF
lab_commit "$CLONE_CORE" "Add core test suite"

# Branch develop (+1 commit)
git -C "$CLONE_CORE" checkout -b develop
echo "# Develop branch update" >> "$CLONE_CORE/src/core.py"
lab_commit "$CLONE_CORE" "Develop branch improvements"

# Branch feature/fast-cache (+1 commit)
git -C "$CLONE_CORE" checkout -b feature/fast-cache main
cat << 'EOF' > "$CLONE_CORE/src/cache.py"
class FastCache:
    pass
EOF
lab_commit "$CLONE_CORE" "Implement fast in-memory cache"

git -C "$CLONE_CORE" push --all origin

# ── 2. Submodule: ui-kit ──
lab_bare ui-kit
CLONE_UI="$SCENARIO_WORK/ui-kit"
git clone "$SCENARIO_REMOTES/ui-kit" "$CLONE_UI"

mkdir -p "$CLONE_UI/components" "$CLONE_UI/themes"
cat << 'EOF' > "$CLONE_UI/README.md"
# UI Kit
Component styling and design system tokens.
EOF
cat << 'EOF' > "$CLONE_UI/components/button.css"
.btn { padding: 8px 16px; border-radius: 4px; }
EOF
lab_commit "$CLONE_UI" "Initial UI kit with button styles"

cat << 'EOF' > "$CLONE_UI/components/modal.css"
.modal { position: fixed; inset: 0; background: rgba(0,0,0,0.5); }
EOF
lab_commit "$CLONE_UI" "Add modal component styles"

cat << 'EOF' > "$CLONE_UI/components/table.css"
.table { width: 100%; border-collapse: collapse; }
EOF
lab_commit "$CLONE_UI" "Add table styling"

# Tag v1.0.0 on commit 3
git -C "$CLONE_UI" tag -a v1.0.0 -m "Release v1.0.0"

# Branch develop (+1 commit)
git -C "$CLONE_UI" checkout -b develop
cat << 'EOF' > "$CLONE_UI/components/drawer.css"
.drawer { position: absolute; top: 0; bottom: 0; width: 300px; }
EOF
lab_commit "$CLONE_UI" "Add drawer component"

# Branch feature/dark-theme (+1 commit)
git -C "$CLONE_UI" checkout -b feature/dark-theme main
cat << 'EOF' > "$CLONE_UI/themes/dark.css"
:root { --bg: #1f1f1f; --fg: #cccccc; }
EOF
lab_commit "$CLONE_UI" "Add dark theme palette"

git -C "$CLONE_UI" push --all origin
git -C "$CLONE_UI" push --tags origin

# ── 3. Submodule: net-utils ──
lab_bare net-utils
CLONE_NET="$SCENARIO_WORK/net-utils"
git clone "$SCENARIO_REMOTES/net-utils" "$CLONE_NET"

mkdir -p "$CLONE_NET/src"
cat << 'EOF' > "$CLONE_NET/README.md"
# Net Utils
Network communication utilities and retry policies.
EOF
cat << 'EOF' > "$CLONE_NET/src/client.go"
package netutils
type Client struct{}
EOF
lab_commit "$CLONE_NET" "Initial commit for net-utils"

cat << 'EOF' > "$CLONE_NET/src/retry.go"
package netutils
func Retry() bool { return true }
EOF
lab_commit "$CLONE_NET" "Add retry logic with backoff"
# (Commit 2: this will be the commit pinned in mainRemote!)

cat << 'EOF' > "$CLONE_NET/src/dns.go"
package netutils
func Resolve(host string) string { return "127.0.0.1" }
EOF
lab_commit "$CLONE_NET" "Add dns resolver helper"

# Branch develop (+1 commit)
git -C "$CLONE_NET" checkout -b develop
cat << 'EOF' > "$CLONE_NET/src/http2.go"
package netutils
type HTTP2Transport struct{}
EOF
lab_commit "$CLONE_NET" "Add HTTP2 transport support"

# Branch feature/quic (+1 commit)
git -C "$CLONE_NET" checkout -b feature/quic main
cat << 'EOF' > "$CLONE_NET/src/quic.go"
package netutils
type QuicConn struct{}
EOF
lab_commit "$CLONE_NET" "Add QUIC experimental protocol"

git -C "$CLONE_NET" push --all origin

# ── 4. Submodule: theme-assets ──
lab_bare theme-assets
CLONE_THEME="$SCENARIO_WORK/theme-assets"
git clone "$SCENARIO_REMOTES/theme-assets" "$CLONE_THEME"

mkdir -p "$CLONE_THEME/icons" "$CLONE_THEME/palette" "$CLONE_THEME/illustrations"
cat << 'EOF' > "$CLONE_THEME/README.md"
# Theme Assets
Icons and color tokens.
EOF
cat << 'EOF' > "$CLONE_THEME/icons/app.svg"
<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="10"/></svg>
EOF
lab_commit "$CLONE_THEME" "Add initial app icon"

cat << 'EOF' > "$CLONE_THEME/icons/chevron.svg"
<svg viewBox="0 0 24 24"><path d="M9 18l6-6-6-6"/></svg>
EOF
lab_commit "$CLONE_THEME" "Add chevron navigation icon"

cat << 'EOF' > "$CLONE_THEME/palette/colors.json"
{ "primary": "#0078D4", "danger": "#F85149" }
EOF
lab_commit "$CLONE_THEME" "Add core color tokens"

# Branch develop (+2 commits)
git -C "$CLONE_THEME" checkout -b develop
cat << 'EOF' > "$CLONE_THEME/icons/search.svg"
<svg viewBox="0 0 24 24"><circle cx="11" cy="11" r="8"/><path d="M21 21l-4.35-4.35"/></svg>
EOF
lab_commit "$CLONE_THEME" "Add search icon to develop"

cat << 'EOF' > "$CLONE_THEME/palette/neon.json"
{ "neon-green": "#39FF14", "neon-pink": "#FF1493" }
EOF
lab_commit "$CLONE_THEME" "Add neon accent palette"

# Branch feature/vector-assets (+1 commit)
git -C "$CLONE_THEME" checkout -b feature/vector-assets main
cat << 'EOF' > "$CLONE_THEME/illustrations/hero.svg"
<svg viewBox="0 0 100 100"><rect width="100" height="100" fill="#0078D4"/></svg>
EOF
lab_commit "$CLONE_THEME" "Add hero illustration vector"

git -C "$CLONE_THEME" push --all origin

# ── 5. Build mainRemote ──
lab_bare mainRemote
CLONE_MAIN="$SCENARIO_WORK/main-repo"
git clone "$SCENARIO_REMOTES/mainRemote" "$CLONE_MAIN"

mkdir -p "$CLONE_MAIN/src" "$CLONE_MAIN/libs"
cat << 'EOF' > "$CLONE_MAIN/README.md"
# Main Application
Modular application utilizing 4 git submodules.
EOF
cat << 'EOF' > "$CLONE_MAIN/src/app.txt"
Main Application Entrypoint
Version: 1.0.0
EOF
cat << 'EOF' > "$CLONE_MAIN/build.cfg"
target=release
opt_level=2
EOF
lab_commit "$CLONE_MAIN" "Initial commit: application scaffolding"

# Add submodules using relative URLs (../<name>)
git -C "$CLONE_MAIN" submodule add ../core-lib libs/core-lib
git -C "$CLONE_MAIN" submodule add ../ui-kit libs/ui-kit
git -C "$CLONE_MAIN" submodule add ../net-utils libs/net-utils
git -C "$CLONE_MAIN" submodule add ../theme-assets libs/theme-assets

# Pin net-utils one commit behind main (main~1)
NET_PIN_COMMIT="$(git -C "$SCENARIO_REMOTES/net-utils" rev-parse main~1)"
(cd "$CLONE_MAIN/libs/net-utils" && git checkout "$NET_PIN_COMMIT")
git -C "$CLONE_MAIN" add libs/net-utils

# Set branch = develop for theme-assets in .gitmodules
git -C "$CLONE_MAIN" config -f .gitmodules submodule.libs/theme-assets.branch develop
(cd "$CLONE_MAIN/libs/theme-assets" && git checkout develop)
git -C "$CLONE_MAIN" add .gitmodules libs/theme-assets

lab_commit "$CLONE_MAIN" "Add core-lib, ui-kit, net-utils, and theme-assets submodules"

# Additional commits on main
echo "build_number=101" >> "$CLONE_MAIN/build.cfg"
lab_commit "$CLONE_MAIN" "Increment build number"

echo "export DEBUG_LOG=0" >> "$CLONE_MAIN/src/app.txt"
lab_commit "$CLONE_MAIN" "Configure runtime log level"

echo "Architecture documentation updated." >> "$CLONE_MAIN/README.md"
lab_commit "$CLONE_MAIN" "Update documentation details"

# Branch develop on mainRemote (+1 commit)
git -C "$CLONE_MAIN" checkout -b develop
echo "target=development" >> "$CLONE_MAIN/build.cfg"
lab_commit "$CLONE_MAIN" "Switch build configuration to development"

# Branch feature/upgrade-ui-kit (moves ui-kit to develop)
git -C "$CLONE_MAIN" checkout -b feature/upgrade-ui-kit main
UI_DEV_COMMIT="$(git -C "$SCENARIO_REMOTES/ui-kit" rev-parse develop)"
(cd "$CLONE_MAIN/libs/ui-kit" && git checkout "$UI_DEV_COMMIT")
git -C "$CLONE_MAIN" add libs/ui-kit
lab_commit "$CLONE_MAIN" "Upgrade ui-kit submodule pointer to develop branch"

# Push all branches and tags to mainRemote
git -C "$CLONE_MAIN" push --all origin
git -C "$CLONE_MAIN" push --tags origin

# ── Self-checks ──
echo "Verifying scenario state..."

# 1. Verify fresh recursive clone of mainRemote into scratch
VERIFY_DIR="$SCENARIO_WORK/verify"
git clone --recurse-submodules "$SCENARIO_REMOTES/mainRemote" "$VERIFY_DIR"

lab_assert "core-lib submodule file exists" [ -f "$VERIFY_DIR/libs/core-lib/src/core.py" ]
lab_assert "ui-kit submodule file exists" [ -f "$VERIFY_DIR/libs/ui-kit/components/button.css" ]
lab_assert "net-utils submodule file exists" [ -f "$VERIFY_DIR/libs/net-utils/src/client.go" ]
lab_assert "theme-assets submodule file exists" [ -f "$VERIFY_DIR/libs/theme-assets/icons/app.svg" ]

# 2. Verify net-utils is pinned at main~1
NET_HEAD="$(git -C "$VERIFY_DIR/libs/net-utils" rev-parse HEAD)"
NET_EXPECTED="$(git -C "$SCENARIO_REMOTES/net-utils" rev-parse main~1)"
lab_assert "net-utils submodule is pinned at main~1" [ "$NET_HEAD" = "$NET_EXPECTED" ]

# 3. Verify theme-assets has branch = develop in .gitmodules
THEME_BRANCH="$(git -C "$VERIFY_DIR" config -f .gitmodules submodule.libs/theme-assets.branch)"
lab_assert "theme-assets submodule tracking branch is develop" [ "$THEME_BRANCH" = "develop" ]

# 4. Verify feature/upgrade-ui-kit moves ui-kit to develop
git -C "$VERIFY_DIR" checkout feature/upgrade-ui-kit
UI_HEAD="$(git -C "$VERIFY_DIR" rev-parse :libs/ui-kit)"
UI_EXPECTED="$(git -C "$SCENARIO_REMOTES/ui-kit" rev-parse develop)"
lab_assert "feature/upgrade-ui-kit points ui-kit to develop" [ "$UI_HEAD" = "$UI_EXPECTED" ]

# 5. Verify tags and branches in remotes
UI_TAG="$(git -C "$SCENARIO_REMOTES/ui-kit" rev-parse --verify refs/tags/v1.0.0)"
lab_assert "ui-kit remote has tag v1.0.0" [ -n "$UI_TAG" ]

CORE_DEV="$(git -C "$SCENARIO_REMOTES/core-lib" rev-parse --verify refs/heads/develop)"
lab_assert "core-lib has develop branch" [ -n "$CORE_DEV" ]

# 6. Verify Local 1 and Local 2 remain empty
L1_COUNT="$(ls -A "$SCENARIO_LOCAL1" 2>/dev/null || true)"
L2_COUNT="$(ls -A "$SCENARIO_LOCAL2" 2>/dev/null || true)"
lab_assert "Local 1 remains empty" [ -z "$L1_COUNT" ]
lab_assert "Local 2 remains empty" [ -z "$L2_COUNT" ]

lab_done
