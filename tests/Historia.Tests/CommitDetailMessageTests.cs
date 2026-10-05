namespace Historia.Tests;

[Trait("tier", "git")]
public class CommitDetailMessageTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    private static CommitDetail ProjectCommitDetail(Repository repo, GitObjectId id)
    {
        var commit = repo.Lookup<Commit>(id.ToLibGit2())
            ?? throw new HistoriaGitException(GitErrorKind.RefNotFound, $"Commit {id} not found.");

        var fullMessage = commit.Message ?? "";
        var lines = fullMessage.Replace("\r\n", "\n").Split('\n');
        var blank = lines.Length > 1 ? Array.FindIndex(lines, 1, string.IsNullOrWhiteSpace) : -1;
        var messageBody = blank >= 0 ? string.Join('\n', lines[(blank + 1)..]).Trim() : "";

        return new CommitDetail
        {
            Id = commit.Id.ToGitObjectId(),
            Message = fullMessage.TrimEnd('\n', '\r'),
            MessageShort = commit.MessageShort,
            MessageBody = messageBody,
            AuthorName = commit.Author.Name,
            AuthorEmail = commit.Author.Email,
            AuthorWhen = commit.Author.When,
            CommitterName = commit.Committer.Name,
            CommitterEmail = commit.Committer.Email,
            CommitWhen = commit.Committer.When,
            ParentIds = commit.Parents.Select(p => p.Id.ToGitObjectId()).ToList(),
        };
    }

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

        var detail = ProjectCommitDetail(git, commitId);

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

        var detail = ProjectCommitDetail(git, commitId);

        Assert.Equal("a\nb\n\nbody", detail.Message);
        Assert.NotEqual(detail.Message, detail.MessageShort);
        Assert.Equal("a b", detail.MessageShort);
        Assert.Equal("body", detail.MessageBody);

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

        var detail = ProjectCommitDetail(git, commitId);

        Assert.Equal("wip", detail.Message);
        Assert.Equal("", detail.MessageBody);

        await using var ctx = _host.Open(repo.Path);
        var cliDetail = await ctx.Gateway.GetCommitDetailAsync(commitId);
        Assert.Equal(detail.Message, cliDetail.Message);
        Assert.Equal(detail.MessageBody, cliDetail.MessageBody);
    }
}
