#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export SCENARIO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
export SCENARIO_LIB="$SCENARIO_ROOT/lib/scenario.sh"
export SCENARIO_TEMPLATES="$SCENARIO_ROOT/templates"
export SCENARIO_NAME="_base"

# Create a temporary sandbox for building the template
SANDBOX="$(mktemp -d 2>/dev/null || mktemp -d -t 'lab_sandbox')"
trap 'rm -rf "$SANDBOX"' EXIT

export SCENARIO_LOCAL1="$SANDBOX/local1"
export SCENARIO_LOCAL2="$SANDBOX/local2"
export SCENARIO_REMOTES="$SANDBOX/remotes"

mkdir -p "$SCENARIO_LOCAL1" "$SCENARIO_LOCAL2" "$SCENARIO_REMOTES"

source "$SCENARIO_LIB"
lab_begin

echo "Creating bare mainRemote..."
lab_bare mainRemote

echo "Creating initial commit with README.md..."
INIT_CLONE="$SCENARIO_WORK/init-clone"
git clone "$SCENARIO_REMOTES/mainRemote" "$INIT_CLONE"

cat << 'EOF' > "$INIT_CLONE/README.md"
# ScenarioLab Base Repository

This is the default shared base repository for Historia scenarios.
EOF

lab_commit "$INIT_CLONE" "Initial commit: add README.md"
git -C "$INIT_CLONE" push -u origin main

echo "Cloning to Local 1 and Local 2..."
git clone "$SCENARIO_REMOTES/mainRemote" "$SCENARIO_LOCAL1"
git clone "$SCENARIO_REMOTES/mainRemote" "$SCENARIO_LOCAL2"

echo "Saving _base template..."
lab_save_template _base

echo "Base template generated successfully in $SCENARIO_TEMPLATES/_base."
