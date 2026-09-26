namespace Historia.Tests;

/// <summary>
/// The tag list comes from the ref snapshot (<see cref="TagProjection"/>), off the repo gate.
/// It must carry exactly the values LibGit2Sharp's <see cref="LibGit2Service.ProjectTags"/> gave
/// — the sidebar compares refreshes structurally and <c>RepoStateCache</c> persists every field —
/// and fall back to LibGit2Sharp only when git.exe is missing.
/// </summary>
[Trait("tier", "git")]
public sealed class TagListParityTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    public TagListParityTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("tag-shapes-v2", b =>
        {
            RefSnapshotProviderTests.BuildRefShapes(b);
            var tagger = new Dictionary<string, string>
            {
                ["GIT_COMMITTER_NAME"] = "Tëster Ü",
                ["GIT_COMMITTER_EMAIL"] = "t@example.com",
                ["GIT_COMMITTER_DATE"] = "2026-01-02T03:04:05+05:30",
            };
            b.Git(tagger, "tag", "-a", "-m", "subject line\n\nbody para\nsecond line", "offset-tagger", "HEAD~1");
            b.Git(tagger, "tag", "-a", "-m", "points at a tree", "annotated-tree", "HEAD^{tree}");

            // Stored verbatim, as LibGit2Sharp's ApplyTag (Historia's own tag creation) stores it.
            var messageFile = Path.Combine(TestScratch.NewDirectory("historia-tagmsg-"), "message.txt");
            File.WriteAllBytes(messageFile, "subject\r\n\r\nbody\rwith a lone CR\r\n"u8.ToArray());
            b.Git(tagger, "tag", "-a", "--cleanup=verbatim", "-F", messageFile, "crlf-message", "HEAD~2");
        });
    }

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    private LibGit2Service Lib => _host.Provider.GetRequiredService<LibGit2Service>();

    [Fact]
    public async Task Cli_tags_equal_libgit2_tags()
    {
        await using var ctx = _host.Open(_repo.Path);

        var expected = (await ctx.WithRepositoryAsync(Lib.ProjectTags))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
        var actual = await ctx.Gateway.GetTagsAsync();

        Assert.Equal(expected, actual);
        Assert.Equal(
            expected.Select(t => t.TaggerWhen?.Offset),
            actual.Select(t => t.TaggerWhen?.Offset));

        var offsetTag = actual.Single(t => t.Name == "offset-tagger");
        Assert.Equal("Tëster Ü", offsetTag.TaggerName);
        Assert.Equal(TimeSpan.FromMinutes(330), offsetTag.TaggerWhen!.Value.Offset);
        Assert.Equal("subject line\n\nbody para\nsecond line\n", offsetTag.Message);
        Assert.Contains(actual, t => t.Name == "annotated-tree");
        Assert.Contains(actual, t => t.Name == "nested");
        Assert.Equal("subject\r\n\r\nbody\rwith a lone CR\r\n", actual.Single(t => t.Name == "crlf-message").Message);
    }

    [Fact]
    public async Task GetTagsAsync_does_not_enter_the_repo_gate_when_git_is_present()
    {
        await using var ctx = _host.Open(_repo.Path);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ctx.WithRepositoryAsync(_ =>
        {
            entered.SetResult();
            release.Task.Wait();
            return 0;
        });
        await entered.Task;
        try
        {
            var tags = ctx.Gateway.GetTagsAsync();
            Assert.Same(tags, await Task.WhenAny(tags, Task.Delay(TimeSpan.FromSeconds(20))));
            Assert.NotEmpty(await tags);
        }
        finally
        {
            release.SetResult();
        }
    }

    [Fact]
    public async Task GetTagsAsync_uses_libgit2_when_git_is_missing()
    {
        await using var ctx = _host.Open(_repo.Path);
        var gateway = new GitGateway(
            ctx,
            Lib,
            new NoGitCli(),
            _host.Provider.GetRequiredService<IRepoLayoutService>(),
            _host.Provider.GetRequiredService<ICredentialVaultService>());

        var expected = await ctx.WithRepositoryAsync(Lib.ProjectTags);
        Assert.Equal(expected, await gateway.GetTagsAsync());
    }

    private sealed class NoGitCli : IGitCliService
    {
        public bool IsGitAvailable => false;

        public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("git is missing — nothing may run it");

        public Task<GitCliResult> RunAsync(GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default) =>
            throw new InvalidOperationException("git is missing — nothing may run it");
    }
}
