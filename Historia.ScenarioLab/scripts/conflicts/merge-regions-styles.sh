#!/usr/bin/env bash
# @short: Local1 mid-merge (merge style); Local2 the same merge under zdiff3
# @long:
#  State:
#  - Local1: mid-merge on feature/login-rework with merge.conflictStyle=merge.
#    Conflicted files: src/Auth/LoginService.cs (4 regions), web/app.ts
#    (2 regions), docs/notes.md (1 region). docs/changelog.md auto-merged and staged.
#  - Local2: mid-merge on feature/login-rework with merge.conflictStyle=zdiff3
#    carrying the same conflicts with ||||||| base sections.
#
#  Check:
#  - B1 (Result pane not empty).
#  - B4 (labels HEAD / origin/release/2.0).
#  - R4 (region actions including Use base on Local2, undo, prev/next, counter 7 -> 0).
#  - R5 (resolve half in external editor, reopen).
#  - R7 (Re-create with base on Local1 gives Local2 shape).
#  - Manual checks 1, 3, 4, 5, 7.

source "$SCENARIO_LIB"
lab_begin

lab_use_template _base

# ── 1. Local1 creates initial files on main and pushes ──
mkdir -p "$SCENARIO_LOCAL1/src/Auth" "$SCENARIO_LOCAL1/web" "$SCENARIO_LOCAL1/docs"

cat << 'EOF' > "$SCENARIO_LOCAL1/src/Auth/LoginService.cs"
// LoginService authentication handlers
using System;

public class LoginService
{
    private int maxRetries = 3;

    public void Authenticate()
    {
        Console.WriteLine("Auth started");
        // common block
        Console.WriteLine("Validating tokens...");
    }

    public void OldPaddingBlock()
    {
        Console.WriteLine("Old line 1");
        Console.WriteLine("Old line 2");
    }

    public void IdenticalSection()
    {
        Console.WriteLine("Default ident");
    }
EOF
printf "    public void FinalMethod() { Console.WriteLine(\"Base EOF\"); }" >> "$SCENARIO_LOCAL1/src/Auth/LoginService.cs"

cat << 'EOF' > "$SCENARIO_LOCAL1/web/app.ts"
export function initApp(): void {
    console.log("Initialize app");
}

export function deletedSection(): void {
    console.log("Line to be deleted by ours");
    console.log("Another line to be deleted");
}

export function middleContext(): void {
    console.log("Context 1");
    console.log("Context 2");
    console.log("Context 3");
    console.log("Context 4");
    console.log("Context 5");
    console.log("Context 6");
}

export function secondConflict(): void {
    console.log("Base conflict 2");
}
EOF

cat << 'EOF' > "$SCENARIO_LOCAL1/docs/notes.md"
# Developer Notes

## Section A
This section is auto-merged.

## Context Lines
Context 1
Context 2
Context 3
Context 4

## Section B
This section will conflict.
EOF

cat << 'EOF' > "$SCENARIO_LOCAL1/docs/changelog.md"
# Changelog
EOF

lab_commit "$SCENARIO_LOCAL1" "Initial commit: LoginService, app.ts, notes, changelog"
git -C "$SCENARIO_LOCAL1" push origin main

# ── 2. Local2 branches release/2.0, edits, and pushes ──
git -C "$SCENARIO_LOCAL2" fetch origin
git -C "$SCENARIO_LOCAL2" checkout -b release/2.0 origin/main

cat << 'EOF' > "$SCENARIO_LOCAL2/src/Auth/LoginService.cs"
// LoginService authentication handlers - release 2.0
using System;

public class LoginService
{
    private int maxRetries = 10;

    public void Authenticate()
    {
        Console.WriteLine("Auth started");
        // common block
        Console.WriteLine("Validating tokens...");
    }

    public void OldPaddingBlock()
    {
        Console.WriteLine("Theirs rewritten 1");
        Console.WriteLine("Theirs rewritten 2");
    }

    public void IdenticalSection()
    {
        Console.WriteLine("Identical change on both");
    }
EOF
printf "    public void FinalMethod() { Console.WriteLine(\"Release 2.0 EOF\"); }" >> "$SCENARIO_LOCAL2/src/Auth/LoginService.cs"

cat << 'EOF' > "$SCENARIO_LOCAL2/web/app.ts"
export function initApp(): void {
    console.log("Initialize app");
}

export function deletedSection(): void {
    console.log("Line modified by theirs");
    console.log("Another line modified by theirs");
}

export function middleContext(): void {
    console.log("Context 1");
    console.log("Context 2");
    console.log("Context 3");
    console.log("Context 4");
    console.log("Context 5");
    console.log("Context 6");
}

export function secondConflict(): void {
    console.log("Theirs conflict 2");
}
EOF

cat << 'EOF' > "$SCENARIO_LOCAL2/docs/notes.md"
# Developer Notes

## Section A
This section is auto-merged.

## Context Lines
Context 1
Context 2
Context 3
Context 4

## Section B
Theirs conflict in notes.
EOF

cat << 'EOF' > "$SCENARIO_LOCAL2/docs/changelog.md"
# Changelog

## Release 2.0 Notes
Auto-merged change from release branch.
EOF

lab_commit "$SCENARIO_LOCAL2" "Release 2.0 updates"
git -C "$SCENARIO_LOCAL2" push -u origin release/2.0

# ── 3. Local1 branches feature/login-rework, edits, pushes, fetches ──
git -C "$SCENARIO_LOCAL1" checkout -b feature/login-rework main

cat << 'EOF' > "$SCENARIO_LOCAL1/src/Auth/LoginService.cs"
// LoginService authentication handlers - feature rework
using System;

public class LoginService
{
    private int maxRetries = 5;

    public void Authenticate()
    {
        Console.WriteLine("Auth started");
        Console.WriteLine("Ours auto-merged authentication step");
        // common block
        Console.WriteLine("Validating tokens...");
    }

    public void OldPaddingBlock()
    {
        Console.WriteLine("Ours added 1");
        Console.WriteLine("Ours added 2");
        Console.WriteLine("Ours added 3");
    }

    public void IdenticalSection()
    {
        Console.WriteLine("Identical change on both");
    }
EOF
printf "    public void FinalMethod() { Console.WriteLine(\"Feature rework EOF\"); }" >> "$SCENARIO_LOCAL1/src/Auth/LoginService.cs"

cat << 'EOF' > "$SCENARIO_LOCAL1/web/app.ts"
export function initApp(): void {
    console.log("Initialize app");
}

export function middleContext(): void {
    console.log("Context 1");
    console.log("Context 2");
    console.log("Context 3");
    console.log("Context 4");
    console.log("Context 5");
    console.log("Context 6");
}

export function secondConflict(): void {
    console.log("Ours conflict 2");
}
EOF

cat << 'EOF' > "$SCENARIO_LOCAL1/docs/notes.md"
# Developer Notes

## Section A
This section is auto-merged by ours.

## Context Lines
Context 1
Context 2
Context 3
Context 4

## Section B
Ours conflict in notes.
EOF

lab_commit "$SCENARIO_LOCAL1" "Feature rework updates"
git -C "$SCENARIO_LOCAL1" push -u origin feature/login-rework
git -C "$SCENARIO_LOCAL1" fetch origin

# ── 4. Local1 runs merge (merge style pinned) ──
lab_pin_config "$SCENARIO_LOCAL1" merge.conflictStyle merge
lab_expect_conflict "$SCENARIO_LOCAL1" "merge origin/release/2.0 into feature/login-rework" merge origin/release/2.0

# ── 5. Local2 checks out feature/login-rework and runs merge (zdiff3 pinned) ──
git -C "$SCENARIO_LOCAL2" fetch origin
git -C "$SCENARIO_LOCAL2" checkout -b feature/login-rework origin/feature/login-rework
lab_pin_config "$SCENARIO_LOCAL2" merge.conflictStyle zdiff3
lab_expect_conflict "$SCENARIO_LOCAL2" "merge origin/release/2.0 under zdiff3" merge origin/release/2.0

# ── 6. Self-checks ──
# Both locals in merge state
lab_assert_op "$SCENARIO_LOCAL1" merge
lab_assert_op "$SCENARIO_LOCAL2" merge

# Porcelain status UU for conflicted files
lab_assert_status "$SCENARIO_LOCAL1" src/Auth/LoginService.cs UU
lab_assert_status "$SCENARIO_LOCAL1" web/app.ts UU
lab_assert_status "$SCENARIO_LOCAL1" docs/notes.md UU
lab_assert_status "$SCENARIO_LOCAL2" src/Auth/LoginService.cs UU
lab_assert_status "$SCENARIO_LOCAL2" web/app.ts UU
lab_assert_status "$SCENARIO_LOCAL2" docs/notes.md UU

# Auto-merged file is staged
lab_assert_status "$SCENARIO_LOCAL1" docs/changelog.md "M "
lab_assert_status "$SCENARIO_LOCAL2" docs/changelog.md "M "

# Start-marker counts: 4 / 2 / 1
lab_assert "Local1 LoginService.cs has 4 start markers" [ "$(lab_count_markers "$SCENARIO_LOCAL1/src/Auth/LoginService.cs" '<')" -eq 4 ]
lab_assert "Local1 app.ts has 2 start markers" [ "$(lab_count_markers "$SCENARIO_LOCAL1/web/app.ts" '<')" -eq 2 ]
lab_assert "Local1 notes.md has 1 start marker" [ "$(lab_count_markers "$SCENARIO_LOCAL1/docs/notes.md" '<')" -eq 1 ]

lab_assert "Local2 LoginService.cs has 4 start markers" [ "$(lab_count_markers "$SCENARIO_LOCAL2/src/Auth/LoginService.cs" '<')" -eq 4 ]
lab_assert "Local2 app.ts has 2 start markers" [ "$(lab_count_markers "$SCENARIO_LOCAL2/web/app.ts" '<')" -eq 2 ]
lab_assert "Local2 notes.md has 1 start marker" [ "$(lab_count_markers "$SCENARIO_LOCAL2/docs/notes.md" '<')" -eq 1 ]

# Base-marker counts: 0 / 0 / 0 in Local1, 4 / 2 / 1 in Local2
lab_assert "Local1 LoginService.cs has 0 base markers" [ "$(lab_count_markers "$SCENARIO_LOCAL1/src/Auth/LoginService.cs" '|')" -eq 0 ]
lab_assert "Local1 app.ts has 0 base markers" [ "$(lab_count_markers "$SCENARIO_LOCAL1/web/app.ts" '|')" -eq 0 ]
lab_assert "Local1 notes.md has 0 base markers" [ "$(lab_count_markers "$SCENARIO_LOCAL1/docs/notes.md" '|')" -eq 0 ]

lab_assert "Local2 LoginService.cs has 4 base markers" [ "$(lab_count_markers "$SCENARIO_LOCAL2/src/Auth/LoginService.cs" '|')" -eq 4 ]
lab_assert "Local2 app.ts has 2 base markers" [ "$(lab_count_markers "$SCENARIO_LOCAL2/web/app.ts" '|')" -eq 2 ]
lab_assert "Local2 notes.md has 1 base marker" [ "$(lab_count_markers "$SCENARIO_LOCAL2/docs/notes.md" '|')" -eq 1 ]

lab_done
