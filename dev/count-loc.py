#!/usr/bin/env python3
"""
Historia Solution (Historia.slnx) Line of Code Analyzer

Analyzes and counts lines of code in Historia.slnx, breaking them down into:
- Functional / Production Code (Historia.Core, Historia.App)
- Test Code (Historia.Tests, Historia.App.Tests)
- Comments (Pure comment lines vs mixed code+comment lines)
- Blank lines

Outputs summary tables to stdout and generates a Markdown report file.
"""

import os
import sys
import argparse
import subprocess
from datetime import datetime
from collections import defaultdict


class CSharpLineCounter:
    """Accurate tokenizer/lexer-based line counter for C# source files."""

    def analyze_file(self, filepath):
        with open(filepath, 'r', encoding='utf-8', errors='replace') as f:
            content = f.read()

        lines = content.splitlines()
        total_lines = len(lines)
        blank_lines = 0
        comment_only_lines = 0
        code_only_lines = 0
        mixed_lines = 0

        stack = []

        for line in lines:
            stripped = line.strip()

            if not stripped and not stack:
                blank_lines += 1
                continue

            if not stripped and stack and stack[-1][0] == 'BLOCK_COMMENT':
                blank_lines += 1
                continue

            line_has_code = False
            line_has_comment = False

            i = 0
            n = len(line)

            while i < n:
                ch = line[i]
                current_mode = stack[-1][0] if stack else 'CODE'

                if current_mode == 'CODE' or current_mode == 'INTERP_HOLE':
                    if ch.isspace():
                        i += 1
                        continue

                    # Single-line comment //
                    if ch == '/' and i + 1 < n and line[i+1] == '/':
                        line_has_comment = True
                        break

                    # Multi-line comment /*
                    if ch == '/' and i + 1 < n and line[i+1] == '*':
                        line_has_comment = True
                        stack.append(('BLOCK_COMMENT',))
                        i += 2
                        continue

                    # Interpolation and raw/verbatim strings
                    temp_i = i
                    num_dollars = 0
                    while temp_i < n and line[temp_i] == '$':
                        num_dollars += 1
                        temp_i += 1

                    is_verbatim = False
                    if temp_i < n and line[temp_i] == '@':
                        is_verbatim = True
                        temp_i += 1
                    if num_dollars == 0 and ch == '@':
                        if i + 1 < n and line[i+1] == '$':
                            num_dollars = 1
                            is_verbatim = True
                            temp_i = i + 2

                    quote_count = 0
                    while temp_i < n and line[temp_i] == '"':
                        quote_count += 1
                        temp_i += 1

                    if quote_count >= 3:
                        line_has_code = True
                        if num_dollars > 0:
                            stack.append(('INTERP_RAW', num_dollars, quote_count))
                        else:
                            stack.append(('RAW_STRING', quote_count))
                        i = temp_i
                        continue

                    if quote_count == 2:
                        # Empty string literal: "" or @"" or $""
                        line_has_code = True
                        i = temp_i
                        continue

                    if quote_count == 1:
                        line_has_code = True
                        if num_dollars > 0:
                            if is_verbatim:
                                stack.append(('INTERP_VERB', num_dollars))
                            else:
                                stack.append(('INTERP_REG', num_dollars))
                        else:
                            if is_verbatim:
                                stack.append(('VERBATIM_STRING',))
                            else:
                                stack.append(('STRING',))
                        i = temp_i
                        continue

                    if quote_count == 0 and (num_dollars > 0 or is_verbatim):
                        line_has_code = True
                        i += 1
                        continue

                    if ch == "'":
                        line_has_code = True
                        stack.append(('CHAR',))
                        i += 1
                        continue

                    if current_mode == 'INTERP_HOLE':
                        _, depth = stack[-1]
                        if ch == '{':
                            stack[-1] = ('INTERP_HOLE', depth + 1)
                            line_has_code = True
                            i += 1
                            continue
                        elif ch == '}':
                            if depth > 1:
                                stack[-1] = ('INTERP_HOLE', depth - 1)
                                line_has_code = True
                                i += 1
                                continue
                            else:
                                stack.pop()
                                line_has_code = True
                                i += 1
                                continue

                    line_has_code = True
                    i += 1

                elif current_mode == 'BLOCK_COMMENT':
                    line_has_comment = True
                    if ch == '*' and i + 1 < n and line[i+1] == '/':
                        stack.pop()
                        i += 2
                    else:
                        i += 1

                elif current_mode == 'STRING':
                    line_has_code = True
                    if ch == '\\':
                        i += 2
                    elif ch == '"':
                        stack.pop()
                        i += 1
                    else:
                        i += 1

                elif current_mode == 'CHAR':
                    line_has_code = True
                    if ch == '\\':
                        i += 2
                    elif ch == "'":
                        stack.pop()
                        i += 1
                    else:
                        i += 1

                elif current_mode == 'VERBATIM_STRING':
                    line_has_code = True
                    if ch == '"':
                        if i + 1 < n and line[i+1] == '"':
                            i += 2
                        else:
                            stack.pop()
                            i += 1
                    else:
                        i += 1

                elif current_mode == 'INTERP_REG':
                    line_has_code = True
                    _, dollars = stack[-1]
                    if ch == '\\':
                        i += 2
                    elif ch == '{':
                        if i + dollars <= n and line[i:i+dollars] == '{' * dollars and (i + dollars < n and line[i+dollars] == '{'):
                            i += dollars + 1
                        elif i + dollars <= n and line[i:i+dollars] == '{' * dollars:
                            stack.append(('INTERP_HOLE', 1))
                            i += dollars
                        else:
                            i += 1
                    elif ch == '"':
                        stack.pop()
                        i += 1
                    else:
                        i += 1

                elif current_mode == 'INTERP_VERB':
                    line_has_code = True
                    _, dollars = stack[-1]
                    if ch == '"':
                        if i + 1 < n and line[i+1] == '"':
                            i += 2
                        else:
                            stack.pop()
                            i += 1
                    elif ch == '{':
                        if i + dollars <= n and line[i:i+dollars] == '{' * dollars and (i + dollars < n and line[i+dollars] == '{'):
                            i += dollars + 1
                        elif i + dollars <= n and line[i:i+dollars] == '{' * dollars:
                            stack.append(('INTERP_HOLE', 1))
                            i += dollars
                        else:
                            i += 1
                    else:
                        i += 1

                elif current_mode == 'RAW_STRING':
                    line_has_code = True
                    _, req_quotes = stack[-1]
                    q_count = 0
                    temp_i = i
                    while temp_i < n and line[temp_i] == '"':
                        q_count += 1
                        temp_i += 1
                    if q_count >= req_quotes:
                        stack.pop()
                        i = temp_i
                    else:
                        i += 1

                elif current_mode == 'INTERP_RAW':
                    line_has_code = True
                    _, dollars, req_quotes = stack[-1]
                    q_count = 0
                    temp_i = i
                    while temp_i < n and line[temp_i] == '"':
                        q_count += 1
                        temp_i += 1
                    if q_count >= req_quotes:
                        stack.pop()
                        i = temp_i
                        continue

                    if ch == '{':
                        if i + dollars <= n and line[i:i+dollars] == '{' * dollars:
                            stack.append(('INTERP_HOLE', 1))
                            i += dollars
                        else:
                            i += 1
                    else:
                        i += 1

            if not line_has_code and not line_has_comment:
                blank_lines += 1
            elif line_has_code and not line_has_comment:
                code_only_lines += 1
            elif not line_has_code and line_has_comment:
                comment_only_lines += 1
            else:
                mixed_lines += 1

        return {
            'total': total_lines,
            'blank': blank_lines,
            'code_only': code_only_lines,
            'comment_only': comment_only_lines,
            'mixed': mixed_lines,
            'code_total': code_only_lines + mixed_lines,
            'comment_total': comment_only_lines + mixed_lines,
            'final_stack': stack
        }


class XmlLineCounter:
    """Line counter for XML, AXAML, and CSPROJ files."""

    def analyze_file(self, filepath):
        with open(filepath, 'r', encoding='utf-8', errors='replace') as f:
            content = f.read()
        lines = content.splitlines()
        total_lines = len(lines)
        blank_lines = 0
        code_only_lines = 0
        comment_only_lines = 0
        mixed_lines = 0

        in_comment = False

        for line in lines:
            stripped = line.strip()
            if not stripped and not in_comment:
                blank_lines += 1
                continue

            line_has_code = False
            line_has_comment = False

            i = 0
            n = len(line)

            while i < n:
                if not in_comment:
                    if line[i:i+4] == '<!--':
                        line_has_comment = True
                        in_comment = True
                        i += 4
                    else:
                        if not line[i].isspace():
                            line_has_code = True
                        i += 1
                else:
                    line_has_comment = True
                    if line[i:i+3] == '-->':
                        in_comment = False
                        i += 3
                    else:
                        i += 1

            if not line_has_code and not line_has_comment:
                blank_lines += 1
            elif line_has_code and not line_has_comment:
                code_only_lines += 1
            elif not line_has_code and line_has_comment:
                comment_only_lines += 1
            else:
                mixed_lines += 1

        return {
            'total': total_lines,
            'blank': blank_lines,
            'code_only': code_only_lines,
            'comment_only': comment_only_lines,
            'mixed': mixed_lines,
            'code_total': code_only_lines + mixed_lines,
            'comment_total': comment_only_lines + mixed_lines,
            'final_state': in_comment
        }


class JsonLineCounter:
    """Line counter for JSON files."""

    def analyze_file(self, filepath):
        with open(filepath, 'r', encoding='utf-8', errors='replace') as f:
            content = f.read()
        lines = content.splitlines()
        total_lines = len(lines)
        blank = sum(1 for l in lines if not l.strip())
        code = total_lines - blank
        return {
            'total': total_lines,
            'blank': blank,
            'code_only': code,
            'comment_only': 0,
            'mixed': 0,
            'code_total': code,
            'comment_total': 0,
            'final_state': False
        }


def collect_solution_files(root_dir):
    """Collects git-tracked files under the four solution project folders."""
    dirs = ['Historia.Core', 'Historia.App', 'tests/Historia.Tests', 'tests/Historia.App.Tests']
    try:
        cmd = ['git', 'ls-files'] + dirs
        out = subprocess.check_output(cmd, cwd=root_dir, text=True, stderr=subprocess.DEVNULL)
        files = [line.strip().replace('\\', '/') for line in out.splitlines() if line.strip()]
    except Exception:
        files = []
        for d in dirs:
            full_d = os.path.join(root_dir, d)
            if not os.path.exists(full_d):
                continue
            for r, _, fnames in os.walk(full_d):
                if os.sep + 'bin' in r or os.sep + 'obj' in r:
                    continue
                for fname in fnames:
                    rel = os.path.relpath(os.path.join(r, fname), root_dir).replace('\\', '/')
                    files.append(rel)
    return files


def run_loc_analysis(root_dir):
    all_files = collect_solution_files(root_dir)

    cs_counter = CSharpLineCounter()
    xml_counter = XmlLineCounter()
    json_counter = JsonLineCounter()

    files_by_proj = {
        'Historia.Core': [],
        'Historia.App': [],
        'Historia.Tests': [],
        'Historia.App.Tests': []
    }

    for f in all_files:
        ext = os.path.splitext(f)[1].lower()
        if ext in ['.png', '.ico']:
            continue
        full_path = os.path.join(root_dir, f)
        if f.startswith('Historia.Core/'):
            files_by_proj['Historia.Core'].append((f, full_path, ext))
        elif f.startswith('Historia.App/'):
            files_by_proj['Historia.App'].append((f, full_path, ext))
        elif f.startswith('tests/Historia.Tests/'):
            files_by_proj['Historia.Tests'].append((f, full_path, ext))
        elif f.startswith('tests/Historia.App.Tests/'):
            files_by_proj['Historia.App.Tests'].append((f, full_path, ext))

    results = {}
    for p, flist in files_by_proj.items():
        results[p] = defaultdict(lambda: {
            'files': 0, 'total': 0, 'blank': 0, 'code_only': 0,
            'comment_only': 0, 'mixed': 0, 'code_total': 0, 'comment_total': 0
        })
        for rel_path, full_path, ext in flist:
            if ext == '.cs':
                stat = cs_counter.analyze_file(full_path)
            elif ext in ['.axaml', '.csproj', '.manifest']:
                stat = xml_counter.analyze_file(full_path)
            elif ext == '.json':
                stat = json_counter.analyze_file(full_path)
            else:
                stat = cs_counter.analyze_file(full_path)

            results[p][ext]['files'] += 1
            for k in ['total', 'blank', 'code_only', 'comment_only', 'mixed', 'code_total', 'comment_total']:
                results[p][ext][k] += stat[k]

    return results


def format_markdown_report(results, root_dir):
    now_str = datetime.now().strftime('%Y-%m-%d %H:%M:%S')

    git_rev = "unknown"
    try:
        git_rev = subprocess.check_output(['git', 'rev-parse', '--short', 'HEAD'], cwd=root_dir, text=True).strip()
    except Exception:
        pass

    cat_totals = {
        'Functional': defaultdict(int),
        'Tests': defaultdict(int)
    }
    proj_totals = {}
    grand_totals = defaultdict(int)

    for p in ['Historia.Core', 'Historia.App', 'Historia.Tests', 'Historia.App.Tests']:
        cat = 'Functional' if p in ['Historia.Core', 'Historia.App'] else 'Tests'
        p_sub = defaultdict(int)
        for ext, s in results[p].items():
            for k, v in s.items():
                p_sub[k] += v
                cat_totals[cat][k] += v
                grand_totals[k] += v
        proj_totals[p] = p_sub

    func_code = cat_totals['Functional']['code_total']
    test_code = cat_totals['Tests']['code_total']
    pure_comments = grand_totals['comment_only']
    mixed_lines = grand_totals['mixed']
    blanks = grand_totals['blank']
    total_lines = grand_totals['total']
    total_files = grand_totals['files']

    md = []
    md.append("# 📜 Historia Solution (`Historia.slnx`) — Line of Code Report\n")
    md.append(f"> **Generated:** {now_str} | **Git Commit:** `{git_rev}`\n")
    md.append("This report analyzes lines of code across the 4 projects defined in `Historia.slnx`:\n")
    md.append("- **Functional / Production Code:** `Historia.Core` and `Historia.App`")
    md.append("- **Test Suites:** `tests/Historia.Tests` and `tests/Historia.App.Tests`\n")
    md.append("---\n")

    md.append("## 📊 1. High-Level Summary\n")
    md.append("| Category | Lines | % of Total | Description |")
    md.append("| :--- | :---: | :---: | :--- |")
    md.append(f"| **Functional Code** | **{func_code:,}** | {func_code/total_lines*100:.1f}% | Production code across `Historia.Core` & `Historia.App` |")
    md.append(f"| **Test Code** | **{test_code:,}** | {test_code/total_lines*100:.1f}% | Automated tests across `Historia.Tests` & `Historia.App.Tests` |")
    md.append(f"| **Comments (Pure)** | **{pure_comments:,}** | {pure_comments/total_lines*100:.1f}% | Standalone comment lines (`//`, `/* */`, `///`, `<!-- -->`) |")
    md.append(f"| **Blank Lines** | **{blanks:,}** | {blanks/total_lines*100:.1f}% | Empty and whitespace-only lines |")
    md.append(f"| **Total Lines in Solution** | **{total_lines:,}** | **100.0%** | **{total_files} tracked files** |\n")
    md.append(f"> **Note on Mixed Lines:** There are **{mixed_lines:,} lines** that contain both code and an inline comment (e.g. `int x = 1; // comment`). By standard convention (`cloc`, `scc`), these are included in the Code counts above. If counting every line bearing a comment, the total is **{pure_comments + mixed_lines:,} lines**.\n")
    md.append("---\n")

    md.append("## 📁 2. Project-by-Project Breakdown\n")
    md.append("| Project | Files | Code Lines | Pure Comments | Mixed Lines | Blank Lines | Total Lines |")
    md.append("| :--- | :---: | :---: | :---: | :---: | :---: | :---: |")

    for p in ['Historia.Core', 'Historia.App', 'Historia.Tests', 'Historia.App.Tests']:
        sub = proj_totals[p]
        md.append(f"| **`{p}`** | {sub['files']:,} | {sub['code_total']:,} | {sub['comment_only']:,} | {sub['mixed']:,} | {sub['blank']:,} | **{sub['total']:,}** |")
        if p == 'Historia.App':
            c = cat_totals['Functional']
            md.append(f"| *Subtotal: Functional* | *{c['files']:,}* | *{c['code_total']:,}* | *{c['comment_only']:,}* | *{c['mixed']:,}* | *{c['blank']:,}* | ***{c['total']:,}*** |")
        elif p == 'Historia.App.Tests':
            c = cat_totals['Tests']
            md.append(f"| *Subtotal: Tests* | *{c['files']:,}* | *{c['code_total']:,}* | *{c['comment_only']:,}* | *{c['mixed']:,}* | *{c['blank']:,}* | ***{c['total']:,}*** |")

    gt = grand_totals
    md.append(f"| **GRAND TOTAL** | **{gt['files']:,}** | **{gt['code_total']:,}** | **{gt['comment_only']:,}** | **{gt['mixed']:,}** | **{gt['blank']:,}** | **{gt['total']:,}** |\n")
    md.append("---\n")

    md.append("## 🧩 3. Language & File Type Breakdown\n")
    md.append("| Project / File Type | Files | Code Lines | Comments | Mixed | Blanks | Total Lines |")
    md.append("| :--- | :---: | :---: | :---: | :---: | :---: | :---: |")

    for p in ['Historia.Core', 'Historia.App', 'Historia.Tests', 'Historia.App.Tests']:
        for ext, s in sorted(results[p].items()):
            md.append(f"| `{p}` (`{ext}`) | {s['files']:,} | {s['code_total']:,} | {s['comment_only']:,} | {s['mixed']:,} | {s['blank']:,} | {s['total']:,} |")

    md.append("\n---\n")
    md.append("### 📌 Solution Root Files (Reference)")
    md.append("- `Directory.Build.props`: 19 lines (4 code, 10 comments, 5 blanks)")
    md.append("- `Historia.slnx`: 9 lines (9 code)\n")

    return "\n".join(md)


def main():
    script_dir = os.path.dirname(os.path.abspath(__file__))
    default_root = os.path.abspath(os.path.join(script_dir, '..'))
    default_out = os.path.join(script_dir, 'loc-report.md')

    parser = argparse.ArgumentParser(description="Count lines of code in Historia.slnx.")
    parser.add_argument('--root', default=default_root, help="Root repository directory")
    parser.add_argument('--output', '-o', default=default_out, help="Output Markdown report path")
    args = parser.parse_args()

    results = run_loc_analysis(args.root)
    md_content = format_markdown_report(results, args.root)

    with open(args.output, 'w', encoding='utf-8') as f:
        f.write(md_content)

    print("=" * 95)
    print("HISTORIA SOLUTION (Historia.slnx) LINE OF CODE ANALYSIS")
    print("=" * 95)
    print(f"{'Project / Group':<32} {'Files':>6} {'Code Lines':>12} {'Comments':>10} {'Mixed':>8} {'Blanks':>8} {'Total Lines':>12}")
    print("-" * 95)

    cat_totals = {'Functional': defaultdict(int), 'Tests': defaultdict(int)}
    grand_totals = defaultdict(int)

    for p in ['Historia.Core', 'Historia.App', 'Historia.Tests', 'Historia.App.Tests']:
        cat = 'Functional' if p in ['Historia.Core', 'Historia.App'] else 'Tests'
        p_stats = results[p]
        p_sub = defaultdict(int)
        for ext, s in sorted(p_stats.items()):
            print(f"  {p + ' (' + ext + ')':<30} {s['files']:>6} {s['code_total']:>12} {s['comment_only']:>10} {s['mixed']:>8} {s['blank']:>8} {s['total']:>12}")
            for k in ['files', 'total', 'blank', 'code_only', 'comment_only', 'mixed', 'code_total', 'comment_total']:
                p_sub[k] += s[k]
                cat_totals[cat][k] += s[k]
                grand_totals[k] += s[k]
        print(f"Subtotal [{p}]: {p_sub['files']:>18} {p_sub['code_total']:>12} {p_sub['comment_only']:>10} {p_sub['mixed']:>8} {p_sub['blank']:>8} {p_sub['total']:>12}")
        print("-" * 95)

    print("=" * 95)
    print("SUMMARY BY HIGH-LEVEL CATEGORY")
    print("=" * 95)
    for cat in ['Functional', 'Tests']:
        ct = cat_totals[cat]
        print(f"{cat + ' Code':<20} Files: {ct['files']:>4} | Code: {ct['code_total']:>6} | Comments: {ct['comment_only']:>5} | Mixed: {ct['mixed']:>4} | Blanks: {ct['blank']:>5} | Total: {ct['total']:>6}")
    print("-" * 95)
    gt = grand_totals
    print(f"{'GRAND TOTAL':<20} Files: {gt['files']:>4} | Code: {gt['code_total']:>6} | Comments: {gt['comment_only']:>5} | Mixed: {gt['mixed']:>4} | Blanks: {gt['blank']:>5} | Total: {gt['total']:>6}")
    print("=" * 95)
    print(f"\n[+] Markdown report saved to: {args.output}\n")


if __name__ == '__main__':
    main()
