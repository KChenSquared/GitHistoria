namespace Historia.Tests;

[Trait("tier", "git")]
public class CommitDetailMessageTests
{
    private readonly LibGit2Service _svc = new();

    [Fact]
    public void Message_is_verbatim_for_subject_and_body()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("commit-msg-verbatim", r =>
        {
            r.WriteFile("f.txt", "1\n").Stage();
        });

        using var git = new Repository(repo.Path);
        var sig = new Signature("Test User", "test@example.com", DateTimeOffset.Now);
        const string raw = "feat: x\n\nline one\n\n- bullet\n";
        var commit = git.Commit(raw, sig, sig);

        var detail = _svc.ProjectCommitDetail(git, commit.Id);

        Assert.Equal("feat: x\n\nline one\n\n- bullet", detail.Message);
    }

    [Fact]
    public void Message_keeps_a_multi_line_first_paragraph()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("commit-msg-multiline-p1", r =>
        {
            r.WriteFile("f.txt", "1\n").Stage();
        });

        using var git = new Repository(repo.Path);
        var sig = new Signature("Test User", "test@example.com", DateTimeOffset.Now);
        const string raw = "a\nb\n\nbody";
        var commit = git.Commit(raw, sig, sig);

        var detail = _svc.ProjectCommitDetail(git, commit.Id);

        Assert.Equal("a\nb\n\nbody", detail.Message);
        Assert.NotEqual(detail.Message, detail.MessageShort);
        Assert.Equal("a b", detail.MessageShort);
    }

    [Fact]
    public void Message_single_line()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("commit-msg-single-line", r =>
        {
            r.WriteFile("f.txt", "1\n").Stage();
        });

        using var git = new Repository(repo.Path);
        var sig = new Signature("Test User", "test@example.com", DateTimeOffset.Now);
        const string raw = "wip";
        var commit = git.Commit(raw, sig, sig);

        var detail = _svc.ProjectCommitDetail(git, commit.Id);

        Assert.Equal("wip", detail.Message);
        Assert.Equal("", detail.MessageBody);
    }
}
