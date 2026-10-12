namespace Historia.Tests;

/// <summary>
/// Guard against test code that can hang on a full pipe. A child process whose redirected output
/// is not drained blocks once the pipe fills, and the test run stalls with it. Every test that
/// starts <c>git</c> goes through <c>TestGit</c>, which drains both streams; so under
/// <c>tests/</c> no file other than <c>TestGit.cs</c> starts <c>git</c> directly, and a file that
/// redirects standard output or standard error also reads that stream. The second rule is per
/// file, not per helper: a file with one draining and one non-draining helper for the same stream
/// is not reported.
/// </summary>
[Trait("tier", "unit")]
public class TestProcessGuardTests
{
    private const string RunnerFile = "tests/Historia.Tests/TestGit.cs";
    private const string GuardFile = "tests/Historia.Tests/TestProcessGuardTests.cs";

    private static readonly Regex GitProcess = new(
        @"ProcessStartInfo\s*\(\s*""git""|\bFileName\s*=\s*""git""",
        RegexOptions.Compiled);

    private static readonly Regex RedirectOutput = new(@"\bRedirectStandardOutput\s*=\s*true\b", RegexOptions.Compiled);
    private static readonly Regex RedirectError = new(@"\bRedirectStandardError\s*=\s*true\b", RegexOptions.Compiled);

    // Reads of a Process stream: a Read* call, the raw stream, or the event-based readers.
    private static readonly Regex ReadsOutput = new(
        @"\bStandardOutput\s*\.\s*(?:Read\w*|BaseStream|CopyTo\w*|Peek)\b|\bBeginOutputReadLine\b|\bOutputDataReceived\b",
        RegexOptions.Compiled);

    private static readonly Regex ReadsError = new(
        @"\bStandardError\s*\.\s*(?:Read\w*|BaseStream|CopyTo\w*|Peek)\b|\bBeginErrorReadLine\b|\bErrorDataReceived\b",
        RegexOptions.Compiled);

    private static readonly Regex LineComment = new(@"//[^\n]*", RegexOptions.Compiled);
    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void Tests_start_git_through_the_drained_pipe_runner_and_drain_what_they_redirect()
    {
        var root = RepoRoot();
        var violations = new List<string>();

        foreach (var file in TestSourceFiles(root))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            violations.AddRange(FindViolations(rel, File.ReadAllText(file)));
        }

        Assert.True(violations.Count == 0,
            "Test process violation — start git through TestGit and read every stream you redirect:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void Scanner_covers_both_test_projects_and_the_runner()
    {
        var root = RepoRoot();
        var scanned = TestSourceFiles(root)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToList();

        Assert.Contains(RunnerFile, scanned);
        Assert.Contains("tests/Historia.App.Tests/RepoTemplates.cs", scanned);
        Assert.DoesNotContain(GuardFile, scanned);
    }

    [Fact]
    public void The_runner_itself_passes_the_stream_rule()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), RunnerFile));

        Assert.Empty(FindViolations(RunnerFile, text));
    }

    [Theory]
    [InlineData("""var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true };""")]
    [InlineData("""var psi = new ProcessStartInfo( "git", "status");""")]
    [InlineData("""var psi = new ProcessStartInfo { FileName = "git" };""")]
    public void Starting_git_directly_is_reported(string source)
    {
        var violations = FindViolations("tests/Historia.Tests/Fixture.cs", source + " p.StandardOutput.ReadToEnd();");

        Assert.Single(violations);
        Assert.Contains("TestGit", violations[0]);
    }

    [Fact]
    public void The_runner_may_start_git()
    {
        Assert.Empty(FindViolations(RunnerFile, """
            var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            """));
    }

    [Theory]
    [InlineData("RedirectStandardOutput = true", "standard output")]
    [InlineData("RedirectStandardError = true", "standard error")]
    public void A_redirected_stream_that_is_never_read_is_reported(string redirect, string stream)
    {
        var violations = FindViolations("tests/Historia.Tests/Fixture.cs",
            $$"""var psi = new ProcessStartInfo("cmd") { {{redirect}} }; Process.Start(psi)!.WaitForExit();""");

        Assert.Single(violations);
        Assert.Contains(stream, violations[0]);
    }

    [Theory]
    [InlineData("RedirectStandardOutput = true", "p.StandardOutput.ReadToEnd();")]
    [InlineData("RedirectStandardOutput = true", "await p.StandardOutput.ReadToEndAsync();")]
    [InlineData("RedirectStandardOutput = true", "p.StandardOutput.BaseStream.CopyTo(sink);")]
    [InlineData("RedirectStandardOutput = true", "p.BeginOutputReadLine();")]
    [InlineData("RedirectStandardError = true", "p.StandardError.ReadToEnd();")]
    [InlineData("RedirectStandardError = true", "p.BeginErrorReadLine();")]
    public void A_redirected_stream_that_is_read_is_accepted(string redirect, string read)
    {
        var violations = FindViolations("tests/Historia.Tests/Fixture.cs",
            $$"""var psi = new ProcessStartInfo("cmd") { {{redirect}} }; var p = Process.Start(psi)!; {{read}}""");

        Assert.Empty(violations);
    }

    [Fact]
    public void Reading_the_other_stream_does_not_excuse_a_redirected_one()
    {
        var violations = FindViolations("tests/Historia.Tests/Fixture.cs",
            """var psi = new ProcessStartInfo("cmd") { RedirectStandardOutput = true, RedirectStandardError = true }; p.StandardOutput.ReadToEnd();""");

        Assert.Single(violations);
        Assert.Contains("standard error", violations[0]);
    }

    [Theory]
    [InlineData("""// new ProcessStartInfo("git")""")]
    [InlineData("""/* new ProcessStartInfo("git") */""")]
    [InlineData("""// RedirectStandardOutput = true""")]
    [InlineData("""var result = run.StandardOutput.Trim();""")]
    public void Comments_and_unrelated_text_are_not_reported(string source)
    {
        Assert.Empty(FindViolations("tests/Historia.Tests/Fixture.cs", source));
    }

    /// <summary>Every rule broken by the source in <paramref name="text"/>, as readable lines.</summary>
    private static List<string> FindViolations(string rel, string text)
    {
        var violations = new List<string>();
        var code = StripComments(text);

        if (rel != RunnerFile)
        {
            foreach (Match m in GitProcess.Matches(code))
                violations.Add($"{rel}:{LineOf(code, m.Index)}: starts git directly — use TestGit.Run or TestGit.RunChecked.");
        }

        var redirectOutput = RedirectOutput.Match(code);
        if (redirectOutput.Success && !ReadsOutput.IsMatch(code))
            violations.Add($"{rel}:{LineOf(code, redirectOutput.Index)}: redirects standard output but never reads it — an undrained pipe blocks the child once it fills.");

        var redirectError = RedirectError.Match(code);
        if (redirectError.Success && !ReadsError.IsMatch(code))
            violations.Add($"{rel}:{LineOf(code, redirectError.Index)}: redirects standard error but never reads it — an undrained pipe blocks the child once it fills.");

        return violations;
    }

    private static string StripComments(string text) =>
        LineComment.Replace(BlockComment.Replace(text, m => new string('\n', m.Value.Count(c => c == '\n'))), "");

    private static int LineOf(string text, int index) => text[..index].Count(c => c == '\n') + 1;

    private static IEnumerable<string> TestSourceFiles(string root)
    {
        var testsDir = Path.Combine(root, "tests");
        foreach (var file in Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel == GuardFile)
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
