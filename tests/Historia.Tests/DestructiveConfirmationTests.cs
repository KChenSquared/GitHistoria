namespace Historia.Tests;

/// <summary>
/// Constitution <b>Rule 00</b> guard, half 1 (call sites). Every destructive action must go
/// through the single entry point <c>IDialogService.ConfirmDestructiveAsync</c> (or
/// <c>ChooseAsync</c> with <c>DialogIcon.Warning</c>), which hard-wires the red
/// <c>triangle-alert</c>, Cancel-as-primary/Enter, and the plain confirm button. No call site
/// may hand-roll that treatment on the plain <c>ConfirmAsync</c> overload — if it does, the
/// contract can silently drift at that one dialog. The dialog-builder half of the rule is
/// guarded by <c>HistoriaApp.Tests.DestructiveConfirmDialogTests</c>.
/// </summary>
public class DestructiveConfirmationTests
{
    // The only files allowed to name the opt-in knobs: the interface that declares them and the
    // one implementation that consumes them.
    private static readonly string[] AllowedFiles =
    {
        "Historia.Core/Services/UI/IDialogService.cs",
        "Historia.App/Platform/AvaloniaDialogService.cs",
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

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
