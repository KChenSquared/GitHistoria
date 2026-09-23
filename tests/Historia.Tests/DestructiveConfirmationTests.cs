namespace Historia.Tests;

/// <summary>
/// Constitution <b>Rule 00</b> guard, half 1 (call sites). Every destructive action must go
/// through the single entry point <c>IDialogService.ConfirmDestructiveAsync</c> (or
/// <c>ChooseAsync</c> with <c>DialogIcon.Warning</c>), which hard-wires the red
/// <c>warning</c>, Cancel-as-primary/Enter, and the plain confirm button. No call site
/// may hand-roll that treatment on the plain <c>ConfirmAsync</c> overload — if it does, the
/// contract can silently drift at that one dialog. The dialog-builder half of the rule is
/// guarded by <c>HistoriaApp.Tests.DestructiveConfirmDialogTests</c>.
/// </summary>
[Trait("tier", "unit")]
public class DestructiveConfirmationTests
{
    // The only files allowed to name the opt-in knobs: the interface that declares them and the
    // one implementation that consumes them.
    private static readonly string[] AllowedFiles =
    {
        "Historia.Core/Services/UI/IDialogService.cs",
        "Historia.App/Platform/AvaloniaDialogService.cs",
        "Historia.App/Services/UnsavedWorkPrompt.cs", // M2.2: the multi-remedy form IDialogService.ChooseAsync documents; this file is its one caller
    };

    private static readonly (Regex pattern, string what)[] Forbidden =
    {
        (new(@"destructive\s*:\s*true", RegexOptions.Compiled), "`destructive: true` on ConfirmAsync"),
        (new(@"\bdefaultToCancel\s*:", RegexOptions.Compiled), "`defaultToCancel:` argument"),
        (new(@"DialogIcon\.Warning", RegexOptions.Compiled), "`DialogIcon.Warning` argument"),
    };

    [Fact]
    public void Destructive_confirmations_only_go_through_the_one_entry_point()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var file in SourceFiles(root))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (AllowedFiles.Contains(rel))
                continue;

            var text = File.ReadAllText(file);
            foreach (var (pattern, what) in Forbidden)
                if (pattern.IsMatch(text))
                    offenders.Add($"{rel}: {what} — use IDialogService.ConfirmDestructiveAsync instead (constitution Rule 00).");
        }

        Assert.True(offenders.Count == 0,
            "Rule 00 violation — a destructive confirmation is hand-rolled instead of routed through ConfirmDestructiveAsync:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_one_entry_point_exists_and_is_used()
    {
        var root = RepoRoot();
        var used = SourceFiles(root).Any(f =>
            Path.GetRelativePath(root, f).Replace('\\', '/').StartsWith("Historia.App/", StringComparison.Ordinal)
            && File.ReadAllText(f).Contains("ConfirmDestructiveAsync(", StringComparison.Ordinal));

        Assert.True(used, "No call site uses IDialogService.ConfirmDestructiveAsync — the Rule 00 entry point has no callers.");
    }

    // #23's call-site guard: every call to one of these destructive service methods must sit in
    // a method whose own text also calls ConfirmDestructiveAsync — catching a call site with NO
    // confirmation at all (what #19 submodule Remove, #20 rebase Start and #22 pushed-amend
    // looked like before they were fixed), which the hand-rolled-styling guard above can't see.
    // Patterns are anchored to the actual call (receiver + method name), not a bare method-name
    // substring, so e.g. BranchMenu's auto-stash `ApplyAndDropAsync` recovery (cleanup of a
    // stash Historia itself made moments earlier, not the user discarding anything) never
    // matches the stash `DropAsync` pattern and needs no allowlist entry.
    private static readonly (Regex Pattern, string What)[] DestructiveCallSites =
    {
        (new(@"_submodules\.RemoveAsync\s*\(", RegexOptions.Compiled), "submodule RemoveAsync"),
        (new(@"_worktrees\.RemoveAsync\s*\(", RegexOptions.Compiled), "worktree RemoveAsync"),
        (new(@"_rebase\.StartAsync\s*\(", RegexOptions.Compiled), "rebase StartAsync"),
        (new(@"\.DiscardAsync\s*\(", RegexOptions.Compiled), "DiscardAsync"),
        (new(@"\.DeleteBranchAsync\s*\(", RegexOptions.Compiled), "DeleteBranchAsync"),
        (new(@"\.DeleteTagAsync\s*\(", RegexOptions.Compiled), "DeleteTagAsync"),
        (new(@"_stashes\.DropAsync\s*\(", RegexOptions.Compiled), "stash DropAsync"),
        (new(@"_stashes\.ClearAsync\s*\(", RegexOptions.Compiled), "stash ClearAsync"),
        (new(@"\.ResetAsync\s*\(", RegexOptions.Compiled), "ResetAsync"),
        (new(@"\.ResolveWithSideAsync\s*\(", RegexOptions.Compiled), "ResolveWithSideAsync"),
        (new(@"\.RecreateConflictAsync\s*\(", RegexOptions.Compiled), "RecreateConflictAsync"),
        (new(@"\.AbortOperationAsync\s*\(", RegexOptions.Compiled), "AbortOperationAsync"),
        // Bounded lookahead (not a full parse) for the Force flag on the same statement, so a
        // plain push (no Force argument at all) never has to sit behind a confirm.
        (new(@"PushAsync\s*\([^;]{0,400}?Force", RegexOptions.Compiled | RegexOptions.Singleline), "force PushAsync"),
    };

    /// <summary>Explicit, commented allowlist for a call site this scan would otherwise flag.
    /// Empty today — every current call site above sits in a confirming method — kept as the
    /// documented escape hatch the plan calls for, keyed by a short marker string unique to the
    /// call site's surrounding text.</summary>
    private static readonly string[] AllowlistMarkers = Array.Empty<string>();

    [Fact]
    public void Every_destructive_call_site_confirms_in_its_own_method()
    {
        var root = RepoRoot();
        var appDir = Path.Combine(root, "Historia.App");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Contains("/bin/") || rel.Contains("/obj/"))
                continue;

            var text = File.ReadAllText(file);
            var methodSpans = FindMethodSpans(text);

            foreach (var (pattern, what) in DestructiveCallSites)
            {
                foreach (Match m in pattern.Matches(text))
                {
                    if (AllowlistMarkers.Any(marker => IsNear(text, m.Index, marker)))
                        continue;

                    var method = EnclosingMethodText(text, methodSpans, m.Index);
                    if (!method.Contains("ConfirmDestructiveAsync(", StringComparison.Ordinal))
                    {
                        var callLine = text[..m.Index].Count(c => c == '\n') + 1;
                        offenders.Add($"{rel}:{callLine}: {what} has no ConfirmDestructiveAsync in its enclosing method.");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Rule 00 violation — a destructive call site has no confirmation (constitution Rule 00):\n  "
            + string.Join("\n  ", offenders));
    }

    private static bool IsNear(string text, int index, string marker)
    {
        var from = Math.Max(0, index - 400);
        var to = Math.Min(text.Length, index + 400);
        return text.IndexOf(marker, from, to - from, StringComparison.Ordinal) >= 0;
    }

    /// <summary>Method signature start: an accessibility modifier, optional
    /// static/sealed/virtual/override/async/partial/new, a return type, a name, then <c>(</c>.
    /// Deliberately not anchored to one line — a multi-line parameter list (several call sites'
    /// enclosing methods have one) only affects where the matching <c>)</c> ends up, found by
    /// balancing parens/braces from here rather than by re-matching more regex against later
    /// lines.</summary>
    private static readonly Regex MethodSignatureStart = new(
        @"(?:public|private|protected|internal)(?:\s+(?:static|sealed|virtual|override|async|partial|new))*\s+[\w<>\[\],\.\?]+\s+\w+\s*\(",
        RegexOptions.Compiled);

    /// <summary>Finds every method body in <paramref name="text"/> as a (start-of-signature,
    /// end-of-closing-brace) span, by balancing parens then braces from each signature match — a
    /// source-scan heuristic, not a parser, but immune to multi-line signatures and to
    /// interpolated-string braces (always balanced pairs, so they never throw a brace count off).</summary>
    private static List<(int Start, int End)> FindMethodSpans(string text)
    {
        var spans = new List<(int, int)>();
        foreach (Match sig in MethodSignatureStart.Matches(text))
        {
            var parenOpen = sig.Index + sig.Length - 1; // index of the '(' the match ends on
            var parenClose = MatchBalanced(text, parenOpen, '(', ')');
            if (parenClose < 0)
                continue;

            var semi = text.IndexOf(';', parenClose);
            var braceOpen = text.IndexOf('{', parenClose);
            if (braceOpen < 0 || (semi >= 0 && semi < braceOpen))
                continue; // an interface/abstract member, or expression-bodied — no block to scan

            var braceClose = MatchBalanced(text, braceOpen, '{', '}');
            if (braceClose < 0)
                continue;

            spans.Add((sig.Index, braceClose));
        }
        return spans;
    }

    private static int MatchBalanced(string text, int openIndex, char open, char close)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == open) depth++;
            else if (text[i] == close && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>The smallest method span containing <paramref name="callIndex"/>, or the whole
    /// file if none was found (a top-level call outside any matched method) — widening the
    /// search can only produce a false pass, never hide a genuinely missing confirm.</summary>
    private static string EnclosingMethodText(string text, List<(int Start, int End)> spans, int callIndex)
    {
        (int Start, int End)? best = null;
        foreach (var span in spans)
        {
            if (span.Start <= callIndex && callIndex <= span.End
                && (best is not { } b || span.End - span.Start < b.End - b.Start))
                best = span;
        }
        return best is { } chosen ? text[chosen.Start..(chosen.End + 1)] : text;
    }

    private static IEnumerable<string> SourceFiles(string root)
    {
        foreach (var project in new[] { "Historia.App", "Historia.Core" })
        {
            var dir = Path.Combine(root, project);
            if (!Directory.Exists(dir))
                continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                if (rel.Contains("/bin/") || rel.Contains("/obj/"))
                    continue;
                yield return f;
            }
        }
    }

    /// <summary>Walks up from the test binary to the repo root. A worktree's own top-level
    /// <c>.git</c> is a FILE (a <c>gitdir:</c> pointer to the main repo's admin dir), not a
    /// directory — checking <c>Directory.Exists</c> alone walks straight past it and lands on
    /// the outer main checkout instead, silently scanning stale source there. Accept either.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var gitEntry = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitEntry) || File.Exists(gitEntry))
                break;
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
