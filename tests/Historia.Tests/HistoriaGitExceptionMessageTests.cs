namespace Historia.Tests;

[Trait("tier", "unit")]
public class HistoriaGitExceptionMessageTests
{
    public static TheoryData<GitErrorKind> AllKinds()
    {
        var data = new TheoryData<GitErrorKind>();
        foreach (var kind in Enum.GetValues<GitErrorKind>())
            data.Add(kind);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void FromCli_names_the_operation_once_and_no_enum_name(GitErrorKind kind)
    {
        var ex = HistoriaGitException.FromCli(new GitCliResult(1, "", "fatal: x", kind, TimeSpan.Zero), "Push");

        Assert.StartsWith("Push failed: ", ex.Message);
        Assert.Single(Regex.Matches(ex.Message, "Push"));
        Assert.EndsWith(".", ex.Message);
        Assert.DoesNotContain($"({kind})", ex.Message);
        Assert.DoesNotContain(kind.ToString(), ex.Message.Replace("Push failed: ", ""));
        Assert.Equal(kind, ex.Kind);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Every_kind_has_its_own_description(GitErrorKind kind)
    {
        var text = HistoriaGitException.Describe(kind);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Equal(char.ToLowerInvariant(text[0]), text[0]);
        Assert.False(text.EndsWith('.'));
    }

    [Fact]
    public void Declined_push_details_start_with_the_remote_messages()
    {
        const string stderr =
            "Enumerating objects: 244, done.\n"
            + "Writing objects: 100% (244/244), done.\n"
            + "remote: Resolving deltas: 100% (3/3), done.\n"
            + "remote: error: File big.bin is 361.72 MB; this exceeds GitHub's file size limit of 100.00 MB\n"
            + "To github-alias:owner/repo.git\n"
            + " ! [remote rejected] main -> main (pre-receive hook declined)\n"
            + "error: failed to push some refs to 'github-alias:owner/repo.git'\n";

        var ex = HistoriaGitException.FromCli(
            new GitCliResult(1, "", stderr, GitErrorClassifier.Classify(1, stderr), TimeSpan.Zero), "Push");

        Assert.Equal(GitErrorKind.RemoteDeclined, ex.Kind);
        Assert.Equal("Push failed: the remote declined it — its reason is in Details.", ex.Message);
        Assert.StartsWith("error: File big.bin is 361.72 MB", ex.Details);
        Assert.Contains(" ! [remote rejected] main -> main (pre-receive hook declined)", ex.Details);
        Assert.DoesNotContain("Enumerating objects", ex.Details);
        Assert.DoesNotContain("Resolving deltas", ex.Details);
    }

    [Fact]
    public void Empty_stderr_falls_back_to_stdout_for_details()
    {
        var ex = HistoriaGitException.FromCli(new GitCliResult(1, "out text", "", GitErrorKind.Unknown, TimeSpan.Zero), "Fetch");
        Assert.Equal("out text", ex.Details);
    }

    [Fact]
    public void NotificationMessage_does_not_double_a_git_exception_prefix()
    {
        Exception ex = HistoriaGitException.FromCli(
            new GitCliResult(128, "", "fatal: unable to access 'x': Could not resolve host: x", GitErrorKind.NetworkUnreachable, TimeSpan.Zero),
            "Push");

        var message = ex.NotificationMessage("Push");

        Assert.Equal("Push failed: couldn't reach the remote.", message);
        Assert.DoesNotContain("Push failed: Push failed", message);
    }

    [Fact]
    public void NotificationMessage_prefixes_a_plain_exception()
        => Assert.Equal("Push failed: boom", new InvalidOperationException("boom").NotificationMessage("Push"));
}
