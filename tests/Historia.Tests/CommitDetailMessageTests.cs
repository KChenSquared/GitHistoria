namespace Historia.Tests;

[Trait("tier", "git")]
public class CommitDetailMessageTests : IAsyncDisposable
{
    private readonly LibGit2Service _svc = new();
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Message_is_verbatim_for_subject_and_body()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("commit-msg-verbatim", r =>
        {
            r.WriteFile("f.txt", "1\n").Stage();
        });

        using var git = new Repository(repo.Path);
        var sig = new Signature("Test User", "test@example.com", DateTimeOffset.Now);
        const string raw = "feat: x\n\nline one\n\n- bullet\n";
        var commit = git.Commit(raw, sig, sig);
        var commitId = commit.Id.ToGitObjectId();

        var detail = _svc.ProjectCommitDetail(git, commitId);

        Assert.Equal("feat: x\n\nline one\n\n- bullet", detail.Message);

        await using var ctx = _host.Open(repo.Path);
        var cliDetail = await ctx.Gateway.GetCommitDetailAsync(commitId);
        Assert.Equal(detail.Message, cliDetail.Message);
        Assert.Equal(detail.MessageShort, cliDetail.MessageShort);
        Assert.Equal(detail.MessageBody, cliDetail.MessageBody);
    }

    [Fact]
    public async Task Message_keeps_a_multi_line_first_paragraph()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("commit-msg-multiline-p1", r =>
        {
            r.WriteFile("f.txt", "1\n").Stage();
        });

        using var git = new Repository(repo.Path);
        var sig = new Signature("Test User", "test@example.com", DateTimeOffset.Now);
        const string raw = "a\nb\n\nbody";
        var commit = git.Commit(raw, sig, sig);
        var commitId = commit.Id.ToGitObjectId();

        var detail = _svc.ProjectCommitDetail(git, commitId);

        Assert.Equal("a\nb\n\nbody", detail.Message);
        Assert.NotEqual(detail.Message, detail.MessageShort);
        Assert.Equal("a b", detail.MessageShort);

        await using var ctx = _host.Open(repo.Path);
        var cliDetail = await ctx.Gateway.GetCommitDetailAsync(commitId);
        Assert.Equal(detail.Message, cliDetail.Message);
        Assert.Equal(detail.MessageShort, cliDetail.MessageShort);
        Assert.Equal(detail.MessageBody, cliDetail.MessageBody);
    }

    [Fact]
    public async Task Message_single_line()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("commit-msg-single-line", r =>
        {
            r.WriteFile("f.txt", "1\n").Stage();
        });

        using var git = new Repository(repo.Path);
        var sig = new Signature("Test User", "test@example.com", DateTimeOffset.Now);
        const string raw = "wip";
        var commit = git.Commit(raw, sig, sig);
        var commitId = commit.Id.ToGitObjectId();

        var detail = _svc.ProjectCommitDetail(git, commitId);

        Assert.Equal("wip", detail.Message);
        Assert.Equal("", detail.MessageBody);

        await using var ctx = _host.Open(repo.Path);
        var cliDetail = await ctx.Gateway.GetCommitDetailAsync(commitId);
        Assert.Equal(detail.Message, cliDetail.Message);
        Assert.Equal(detail.MessageBody, cliDetail.MessageBody);
    }
}
