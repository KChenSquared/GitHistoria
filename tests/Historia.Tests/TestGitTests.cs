namespace Historia.Tests;

/// <summary>The fixture git runner reads every stream while git runs.</summary>
[Trait("tier", "git")]
public sealed class TestGitTests : IDisposable
{
    private readonly string _dir = TestScratch.NewDirectory("testgit-");

    public void Dispose() => TestScratch.Delete(_dir);

    [Fact]
    public void Run_drains_stderr_larger_than_the_pipe_buffer()
    {
        TestGit.RunChecked(_dir, ["init", "-q", "-b", "main"]);
        for (var i = 0; i < 5_000; i++)
            File.WriteAllText(Path.Combine(_dir, $"file{i:D4}.txt"), "line\n");

        // One "LF will be replaced by CRLF" warning per file: the shape that once hung the App suite.
        var result = TestGit.Run(_dir, ["-c", "core.autocrlf=true", "add", "."], timeout: TimeSpan.FromSeconds(60));

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Stderr.Length > 100_000, $"stderr was only {result.Stderr.Length} characters.");
    }

    [Fact]
    public void Run_drains_stdout_larger_than_the_pipe_buffer()
    {
        TestGit.RunChecked(_dir, ["init", "-q", "-b", "main"]);
        var names = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"{i:x40}\n"));

        var result = TestGit.Run(_dir, ["cat-file", "--batch-check"], stdin: names, timeout: TimeSpan.FromSeconds(60));

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Stdout.Length > 64 * 1024, $"stdout was only {result.Stdout.Length} characters.");
        Assert.Contains(" missing", result.Stdout);
    }

    [Fact]
    public void RunChecked_throws_with_stderr_on_a_failing_command()
    {
        TestGit.RunChecked(_dir, ["init", "-q", "-b", "main"]);

        var ex = Assert.Throws<InvalidOperationException>(() => TestGit.RunChecked(_dir, ["rev-parse", "--verify", "nope"]));

        Assert.Contains("rev-parse --verify nope", ex.Message);
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void Run_writes_stdin_and_closes_it()
    {
        TestGit.RunChecked(_dir, ["init", "-q", "-b", "main"]);

        var hash = TestGit.RunChecked(_dir, ["hash-object", "--stdin"], stdin: "hello\n");

        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", hash.Trim());
    }
}
