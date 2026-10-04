namespace Historia.Tests;

[Trait("tier", "unit")]
public class GitStderrTests
{
    private const string PushWithProgress =
        "Enumerating objects: 244, done.\n"
        + "Counting objects:  50% (122/244)\rCounting objects: 100% (244/244), done.\n"
        + "Delta compression using up to 16 threads\n"
        + "Compressing objects: 100% (200/200), done.\n"
        + "Writing objects: 100% (244/244), 600.12 MiB | 12.00 MiB/s, done.\n"
        + "Total 244 (delta 30), reused 0 (delta 0), pack-reused 0\n"
        + "remote: Resolving deltas: 100% (3/3), done.\n"
        + "remote: warning: See https://gh.io/lfs for more information.\n"
        + "remote: error: File big.bin is 361.72 MB; this exceeds GitHub's file size limit of 100.00 MB\n"
        + "remote:\n"
        + "To github-alias:owner/repo.git\n"
        + " ! [remote rejected] main -> main (pre-receive hook declined)\n"
        + "error: failed to push some refs to 'github-alias:owner/repo.git'\n";

    [Theory]
    [InlineData("Enumerating objects: 244, done.")]
    [InlineData("remote: Resolving deltas: 100% (3/3), done.")]
    [InlineData("remote: Counting objects: 100% (5/5), done.")]
    [InlineData("Writing objects:  10% (1/10)\rWriting objects: 100% (10/10), done.")]
    [InlineData("Total 3 (delta 0), reused 0 (delta 0), pack-reused 0")]
    [InlineData("Receiving objects: 100% (12/12), done.")]
    public void Progress_lines_are_recognised(string line) => Assert.True(GitStderr.IsProgressLine(line));

    [Theory]
    [InlineData("remote: error: File big.bin is 361.72 MB")]
    [InlineData("fatal: the remote end hung up unexpectedly")]
    [InlineData(" ! [remote rejected] main -> main (pre-receive hook declined)")]
    public void Diagnostic_lines_are_not_progress(string line) => Assert.False(GitStderr.IsProgressLine(line));

    [Fact]
    public void Clean_drops_progress_and_blank_lines()
    {
        var cleaned = GitStderr.Clean(PushWithProgress);

        Assert.DoesNotContain("Enumerating objects", cleaned);
        Assert.DoesNotContain("Counting objects", cleaned);
        Assert.DoesNotContain("Resolving deltas", cleaned);
        Assert.DoesNotContain("Total 244", cleaned);
        Assert.DoesNotContain("\r", cleaned);
        Assert.DoesNotContain("\n\n", cleaned);
        Assert.StartsWith("remote: warning:", cleaned);
        Assert.Contains("remote: error: File big.bin", cleaned);
        Assert.EndsWith("error: failed to push some refs to 'github-alias:owner/repo.git'", cleaned);
    }

    [Fact]
    public void RemoteMessages_put_errors_first_and_strip_the_prefix()
    {
        var messages = GitStderr.RemoteMessages(PushWithProgress);

        Assert.Equal(
            [
                "error: File big.bin is 361.72 MB; this exceeds GitHub's file size limit of 100.00 MB",
                "warning: See https://gh.io/lfs for more information.",
            ],
            messages);
    }

    [Fact]
    public void SummaryLine_prefers_the_remote_error()
        => Assert.Equal(
            "remote: error: File big.bin is 361.72 MB; this exceeds GitHub's file size limit of 100.00 MB",
            GitStderr.SummaryLine(PushWithProgress + "fatal: something else\n"));

    [Fact]
    public void SummaryLine_prefers_fatal_over_leading_progress()
        => Assert.Equal(
            "fatal: unable to access 'https://x/': Could not resolve host: x",
            GitStderr.SummaryLine(
                "Enumerating objects: 244, done.\nfatal: unable to access 'https://x/': Could not resolve host: x\n"));

    [Fact]
    public void SummaryLine_takes_the_last_error_line()
        => Assert.Equal(
            "error: failed to push some refs to 'x'",
            GitStderr.SummaryLine(" ! [rejected]        main -> main (fetch first)\nerror: failed to push some refs to 'x'\n"));

    [Fact]
    public void SummaryLine_falls_back_to_the_first_non_progress_line()
        => Assert.Equal("some novel text", GitStderr.SummaryLine("Enumerating objects: 3, done.\nsome novel text\nmore\n"));

    [Fact]
    public void Empty_input_yields_empty_output()
    {
        Assert.Equal("", GitStderr.SummaryLine(""));
        Assert.Equal("", GitStderr.Clean(null));
        Assert.Empty(GitStderr.RemoteMessages(null));
    }
}
