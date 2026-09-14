#!/usr/bin/env bash
# Historia.ScenarioLab - Bash Helper Library

_LAB_BASE_CLOCK=1767603600 # 2026-01-05T09:00:00Z
_LAB_CLOCK=$_LAB_BASE_CLOCK

lab_begin() {
    set -euo pipefail

    # 1. Verify required environment variables
    local required_vars=(
        SCENARIO_LOCAL1
        SCENARIO_LOCAL2
        SCENARIO_REMOTES
        SCENARIO_TEMPLATES
        SCENARIO_LIB
        SCENARIO_ROOT
        SCENARIO_NAME
    )

    for var in "${required_vars[@]}"; do
        if [ -z "${!var:-}" ]; then
            echo "[FAIL] Required environment variable '$var' is not set." >&2
            exit 1
        fi
    done

    # 2. Verify that scratch directories are empty
    local check_dirs=("$SCENARIO_LOCAL1" "$SCENARIO_LOCAL2" "$SCENARIO_REMOTES")
    for dir in "${check_dirs[@]}"; do
        if [ -d "$dir" ] && [ "$(ls -A "$dir" 2>/dev/null)" ]; then
            echo "[FAIL] Scratch folder '$dir' is not empty. Execute purges first." >&2
            exit 1
        fi
        mkdir -p "$dir"
    done

    # 3. Deterministic Git configuration
    export GIT_CONFIG_NOSYSTEM=1
    export GIT_CONFIG_GLOBAL="$SCENARIO_ROOT/lib/lab.gitconfig"

    # 4. Scratch work directory
    SCENARIO_WORK="$(mktemp -d 2>/dev/null || mktemp -d -t 'scenario_work')"
    export SCENARIO_WORK
    trap 'rm -rf "$SCENARIO_WORK"' EXIT

    # 5. Initialize deterministic clock
    _LAB_CLOCK=$_LAB_BASE_CLOCK
    lab_tick 0
}

lab_tick() {
    local secs="${1:-3600}"
    _LAB_CLOCK=$(( _LAB_CLOCK + secs ))
    export GIT_AUTHOR_DATE="@${_LAB_CLOCK} +0000"
    export GIT_COMMITTER_DATE="@${_LAB_CLOCK} +0000"
}

lab_commit() {
    local dir="$1"
    local msg="$2"
    lab_tick
    git -C "$dir" add -A
    git -C "$dir" commit -m "$msg"
}

lab_bare() {
    local name="$1"
    local dest="$SCENARIO_REMOTES/$name"
    mkdir -p "$dest"
    git init --bare "$dest"
}

lab_clone() {
    local remote_name="$1"
    local dest="$2"
    local src="$SCENARIO_REMOTES/$remote_name"
    git clone "$src" "$dest"
}

_lab_ensure_git_dirs() {
    local target="$1"
    mkdir -p "$target/refs/heads" "$target/refs/tags" "$target/objects/info" "$target/objects/pack"
    if [ -d "$target/modules" ]; then
        find "$target/modules" -type d -name "refs" 2>/dev/null | while read -r d; do
            mkdir -p "$d/heads" "$d/tags"
        done
        find "$target/modules" -type d -name "objects" 2>/dev/null | while read -r d; do
            mkdir -p "$d/info" "$d/pack"
        done
    fi
}

lab_has_template() {
    local name="${1:-$SCENARIO_NAME}"
    [ -d "$SCENARIO_TEMPLATES/$name" ]
}

lab_use_template() {
    local name="${1:-$SCENARIO_NAME}"
    local tmpl_dir="$SCENARIO_TEMPLATES/$name"

    if [ ! -d "$tmpl_dir" ]; then
        echo "[FAIL] Template '$name' not found at '$tmpl_dir'." >&2
        exit 1
    fi

    # 1. Bare mainRemote
    if [ -d "$tmpl_dir/remote-git" ]; then
        mkdir -p "$SCENARIO_REMOTES/mainRemote"
        cp -r "$tmpl_dir/remote-git/." "$SCENARIO_REMOTES/mainRemote/"
        _lab_ensure_git_dirs "$SCENARIO_REMOTES/mainRemote"
    fi

    # 2. Submodule bare remotes (<sub>-git)
    for sub_dir in "$tmpl_dir"/*-git; do
        if [ -d "$sub_dir" ]; then
            local base
            base="$(basename "$sub_dir")"
            local sub_name="${base%-git}"
            if [ "$sub_name" != "local1" ] && [ "$sub_name" != "local2" ] && [ "$sub_name" != "remote" ]; then
                mkdir -p "$SCENARIO_REMOTES/$sub_name"
                cp -r "$sub_dir/." "$SCENARIO_REMOTES/$sub_name/"
                _lab_ensure_git_dirs "$SCENARIO_REMOTES/$sub_name"
            fi
        fi
    done

    # 3. Local 1
    if [ -d "$tmpl_dir/local1-git" ]; then
        mkdir -p "$SCENARIO_LOCAL1/.git"
        cp -r "$tmpl_dir/local1-git/." "$SCENARIO_LOCAL1/.git/"
        _lab_ensure_git_dirs "$SCENARIO_LOCAL1/.git"
        git -C "$SCENARIO_LOCAL1" remote set-url origin "$SCENARIO_REMOTES/mainRemote"
        git -C "$SCENARIO_LOCAL1" reset --hard
        if git -C "$SCENARIO_LOCAL1" rev-parse --verify HEAD >/dev/null 2>&1; then
            git -C "$SCENARIO_LOCAL1" submodule update --init --recursive 2>/dev/null || true
        fi
    fi

    # 4. Local 2
    if [ -d "$tmpl_dir/local2-git" ]; then
        mkdir -p "$SCENARIO_LOCAL2/.git"
        cp -r "$tmpl_dir/local2-git/." "$SCENARIO_LOCAL2/.git/"
        _lab_ensure_git_dirs "$SCENARIO_LOCAL2/.git"
        git -C "$SCENARIO_LOCAL2" remote set-url origin "$SCENARIO_REMOTES/mainRemote"
        git -C "$SCENARIO_LOCAL2" reset --hard
        if git -C "$SCENARIO_LOCAL2" rev-parse --verify HEAD >/dev/null 2>&1; then
            git -C "$SCENARIO_LOCAL2" submodule update --init --recursive 2>/dev/null || true
        fi
    fi
}

_lab_clean_git_copy() {
    local target="$1"
    rm -rf "$target/hooks"/*.sample 2>/dev/null || true
    rm -rf "$target/logs" 2>/dev/null || true
    rm -f "$target/FETCH_HEAD" "$target/ORIG_HEAD" "$target/COMMIT_EDITMSG" 2>/dev/null || true
}

lab_save_template() {
    local name="${1:-$SCENARIO_NAME}"
    local out_dir="$SCENARIO_TEMPLATES/$name"
    mkdir -p "$out_dir"

    # Save Remotes (mainRemote -> remote-git, <sub> -> <sub>-git)
    if [ -d "$SCENARIO_REMOTES" ]; then
        for r_dir in "$SCENARIO_REMOTES"/*; do
            if [ -d "$r_dir" ]; then
                local r_name
                r_name="$(basename "$r_dir")"
                local target_name
                if [ "$r_name" = "mainRemote" ]; then
                    target_name="remote-git"
                else
                    target_name="${r_name}-git"
                fi

                git -C "$r_dir" pack-refs --all 2>/dev/null || true
                local dest="$out_dir/$target_name"
                rm -rf "$dest"
                mkdir -p "$dest"
                cp -r "$r_dir/." "$dest/"
                _lab_clean_git_copy "$dest"
            fi
        done
    fi

    # Save Local 1 (Local1/.git -> local1-git)
    if [ -d "$SCENARIO_LOCAL1/.git" ]; then
        git -C "$SCENARIO_LOCAL1" pack-refs --all 2>/dev/null || true
        local dest="$out_dir/local1-git"
        rm -rf "$dest"
        mkdir -p "$dest"
        cp -r "$SCENARIO_LOCAL1/.git/." "$dest/"
        _lab_clean_git_copy "$dest"
        # Sanitize remote origin url so no machine-specific absolute path is saved
        git --git-dir="$dest" config remote.origin.url "mainRemote" 2>/dev/null || true
    fi

    # Save Local 2 (Local2/.git -> local2-git)
    if [ -d "$SCENARIO_LOCAL2/.git" ]; then
        git -C "$SCENARIO_LOCAL2" pack-refs --all 2>/dev/null || true
        local dest="$out_dir/local2-git"
        rm -rf "$dest"
        mkdir -p "$dest"
        cp -r "$SCENARIO_LOCAL2/.git/." "$dest/"
        _lab_clean_git_copy "$dest"
        # Sanitize remote origin url
        git --git-dir="$dest" config remote.origin.url "mainRemote" 2>/dev/null || true
    fi

    # Check for ignored files (F8)
    local ignored
    ignored="$(git -C "$SCENARIO_ROOT" status --ignored --porcelain "$out_dir" 2>/dev/null | grep '^!!' || true)"
    if [ -n "$ignored" ]; then
        echo "[WARN] Ignored files detected in saved template '$name':" >&2
        echo "$ignored" >&2
    fi

    # Check for absolute Remotes paths left in configs
    find "$out_dir" -type f -name "config" 2>/dev/null | while read -r cfg; do
        if grep -q -F "$SCENARIO_REMOTES" "$cfg" 2>/dev/null; then
            echo "[WARN] Absolute remotes path found in saved config: $cfg" >&2
        fi
    done
}

lab_assert() {
    local desc="$1"
    shift
    if ! "$@"; then
        echo "[FAIL] $desc" >&2
        exit 1
    fi
}

lab_done() {
    echo ""
    echo "── ScenarioLab Result Summary ──"

    if [ -d "$SCENARIO_LOCAL1/.git" ]; then
        echo "[Local 1 Log]"
        git -C "$SCENARIO_LOCAL1" log --oneline --graph --all -n 15 || true
        echo ""
    fi

    if [ -d "$SCENARIO_LOCAL2/.git" ]; then
        echo "[Local 2 Log]"
        git -C "$SCENARIO_LOCAL2" log --oneline --graph --all -n 15 || true
        echo ""
    fi

    if [ -d "$SCENARIO_REMOTES" ]; then
        for r in "$SCENARIO_REMOTES"/*; do
            if [ -d "$r" ]; then
                local r_name
                r_name="$(basename "$r")"
                echo "[Remote: $r_name Log]"
                git -C "$r" log --oneline --graph --all -n 15 || true
                echo ""
            fi
        done
    fi

    echo "[SUCCESS] Scenario '$SCENARIO_NAME' complete."
}
