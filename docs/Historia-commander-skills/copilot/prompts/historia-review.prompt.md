---
description: Walk through a branch one file at a time in Historia, with a short note on what to look at in each stop.
agent: agent
argument-hint: base branch (default main)
---

Give me a guided review of the changes on this branch against `${input:base:main}`, using Historia to
show each file.

1. List the changes with `git diff --name-status ${input:base:main}...HEAD` in the terminal.
2. Order the files for reading: interfaces and models first, then implementations, then call sites, then
   tests, then configuration and docs. Skip generated files.
3. Show me the ordered list, one line each saying why the file is where it is.
4. Show stop 1: find the fork point with `git merge-base ${input:base:main} HEAD`, then run
   `historia-cmdr compare <merge-base> HEAD <file> --line <first changed line>`
   and add a 2–4 line note on what changed and what to check.
5. Wait for me before each next stop. I will say "next", "back", "skip to N" or "done". Use
   `--no-focus` after the first stop.
6. Collect my comments as I make them and summarize them as follow-ups at the end.

Do not edit any files during the review unless I ask. If `historia-cmdr` exits with code 2, Historia is
unreachable or switched off: tell me once and continue in chat with `git diff` output.
