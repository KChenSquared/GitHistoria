namespace Historia.Tests;

/// <summary>
/// View model guard. A view model's continuations must return to the UI context before they touch
/// bound state, so <c>ConfigureAwait(false)</c> is banned there, and a view model never blocks the
/// UI thread, so <c>.Wait(</c> and <c>GetAwaiter().GetResult()</c> are banned too. <c>.Result</c> is
/// deliberately not banned: it would flag reads of a completed task and any property named
/// <c>Result</c>. Scans every file under <c>Historia.Core/ViewModels</c>.
/// </summary>
[Trait("tier", "unit")]
public class ViewModelGuardTests
{
    private static readonly (Regex Pattern, string What)[] Banned =
    [
        (new(@"\.ConfigureAwait\s*\(\s*false\s*\)", RegexOptions.Compiled),
            "`ConfigureAwait(false)` — a view model's continuation must resume on the UI context"),
        (new(@"\.Wait\s*\(", RegexOptions.Compiled),
            "`.Wait(` — a view model must not block the UI thread"),
        (new(@"\.GetAwaiter\s*\(\s*\)\s*\.GetResult\s*\(", RegexOptions.Compiled),
            "`GetAwaiter().GetResult()` — a view model must not block the UI thread"),
    ];

    [Fact]
    public void View_models_do_not_detach_from_the_ui_context_or_block_it()
    {
        var root = RepoRoot();
        var violations = new List<string>();

        foreach (var file in ViewModelFiles(root))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            violations.AddRange(FindViolations(rel, File.ReadAllText(file)));
        }

        Assert.True(violations.Count == 0,
            "View model violation:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Scanner_covers_the_view_model_folder()
    {
        var root = RepoRoot();
        var scanned = ViewModelFiles(root)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToList();

        Assert.Contains("Historia.Core/ViewModels/ViewModelBase.cs", scanned);
    }

    [Theory]
    [InlineData("var x = await DoAsync().ConfigureAwait(false);", "ConfigureAwait")]
    [InlineData("var x = await DoAsync().ConfigureAwait( false );", "ConfigureAwait")]
    [InlineData("task.Wait();", ".Wait(")]
    [InlineData("task.Wait(TimeSpan.FromSeconds(1));", ".Wait(")]
    [InlineData("var x = DoAsync().GetAwaiter().GetResult();", "GetResult")]
    public void Banned_calls_are_reported(string source, string expected)
    {
        var violations = FindViolations("Historia.Core/ViewModels/Fixture.cs", Wrap(source));

        Assert.Single(violations);
        Assert.Contains(expected, violations[0]);
    }

    [Theory]
    [InlineData("var x = await DoAsync().ConfigureAwait(true);")]
    [InlineData("var x = await DoAsync();")]
    [InlineData("await task.WaitAsync(TimeSpan.FromSeconds(1));")]
    [InlineData("var x = completed.Result;")]
    [InlineData("// never call task.Wait() or ConfigureAwait(false) here")]
    [InlineData("/* DoAsync().GetAwaiter().GetResult() */")]
    public void Allowed_code_and_comments_are_accepted(string source)
    {
        Assert.Empty(FindViolations("Historia.Core/ViewModels/Fixture.cs", Wrap(source)));
    }

    private static string Wrap(string statement) => $"class Fixture {{ void Run() {{\n{statement}\n}} }}";

    /// <summary>Every banned call in <paramref name="text"/>, outside comments, as readable lines.</summary>
    private static List<string> FindViolations(string rel, string text)
    {
        var code = Regex.Replace(text, @"/\*.*?\*/", m => Regex.Replace(m.Value, @"[^\n]", " "), RegexOptions.Singleline);
        code = Regex.Replace(code, @"//[^\n]*", " ");

        var violations = new List<string>();
        foreach (var (pattern, what) in Banned)
        {
            foreach (Match m in pattern.Matches(code))
            {
                var line = code[..m.Index].Count(c => c == '\n') + 1;
                violations.Add($"{rel}:{line}: {what}.");
            }
        }

        return violations;
    }

    private static IEnumerable<string> ViewModelFiles(string root)
    {
        var dir = Path.Combine(root, "Historia.Core", "ViewModels");
        if (!Directory.Exists(dir))
            yield break;

        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Contains("/bin/") || rel.Contains("/obj/"))
                continue;
            yield return file;
        }
    }

    private static string RepoRoot()
    {
        // A worktree's top-level .git is a file, not a directory.
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
