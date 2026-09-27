namespace Historia.Tests;

[Trait("tier", "git")]
public sealed class CommitHeaderReaderTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    private IGitCliService Cli => _host.Provider.GetRequiredService<IGitCliService>();

    [Fact]
    public async Task Reads_root_commit_multiline_empty_body_and_merge_parents()
    {
        GitObjectId rootId = default;
        GitObjectId c2Id = default;
        GitObjectId c3Id = default;
        GitObjectId mergeId = default;

        using var repo = TestRepositoryBuilder.FromTemplate("header-reader-seed", r =>
        {
            // Root commit with empty body.
            r.WriteFile("f1", "root").Stage().Commit("root commit subject");
            rootId = GitObjectId.Parse(r.HeadSha);

            // Commit with multi-line message and non-ASCII author.
            var env = new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = "Tëster Ünîcode",
                ["GIT_AUTHOR_EMAIL"] = "tester@example.com",
            };
            r.WriteFile("f2", "c2").Stage();
            r.Git(env, "commit", "-m", "multi-line subject\n\nbody paragraph 1\nbody paragraph 2\n");
            c2Id = GitObjectId.Parse(r.HeadSha);

            // Branch c3 off root.
            r.Git("checkout", "-b", "feature", rootId.ToString());
            r.WriteFile("f3", "c3").Stage().Commit("feature commit");
            c3Id = GitObjectId.Parse(r.HeadSha);

            // Merge feature into main.
            r.Git("checkout", "main");
            r.Git("merge", "--no-ff", "-m", "Merge feature branch", "feature");
            mergeId = GitObjectId.Parse(r.HeadSha);
        });

        var reader = new CommitHeaderReader(Cli, repo.Path);

        // Read all 4 commits in specific input order: merge, c3, c2, root.
        var headers = await reader.ReadAsync([mergeId, c3Id, c2Id, rootId]);

        Assert.Equal(4, headers.Count);

        // Verify order is preserved.
        Assert.Equal(mergeId, headers[0].Id);
        Assert.Equal(c3Id, headers[1].Id);
        Assert.Equal(c2Id, headers[2].Id);
        Assert.Equal(rootId, headers[3].Id);

        // 1. Merge commit: 2 parents.
        Assert.Equal(2, headers[0].ParentIds.Count);
        Assert.Equal(c2Id, headers[0].ParentIds[0]);
        Assert.Equal(c3Id, headers[0].ParentIds[1]);
        Assert.Equal("Merge feature branch", headers[0].Subject);

        // 2. Feature commit.
        Assert.Single(headers[1].ParentIds);
        Assert.Equal(rootId, headers[1].ParentIds[0]);
        Assert.Equal("feature commit", headers[1].Subject);

        // 3. Multi-line commit with non-ASCII author.
        Assert.Equal("Tëster Ünîcode", headers[2].AuthorName);
        Assert.Equal("tester@example.com", headers[2].AuthorEmail);
        Assert.Equal("multi-line subject", headers[2].Subject);
        Assert.Contains("body paragraph 1", headers[2].Message);
        Assert.Contains("body paragraph 2", headers[2].Message);

        // 4. Root commit: 0 parents.
        Assert.Empty(headers[3].ParentIds);
        Assert.Equal("root commit subject", headers[3].Subject);
    }

    [Fact]
    public async Task Unknown_id_throws_classified_ref_not_found_exception()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("header-reader-err", r =>
            r.WriteFile("f1", "x").Stage().Commit("c1"));

        var reader = new CommitHeaderReader(Cli, repo.Path);
        var fakeId = GitObjectId.Parse("0123456789abcdef0123456789abcdef01234567");

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(() =>
            reader.ReadAsync([fakeId]));

        Assert.Equal(GitErrorKind.RefNotFound, ex.Kind);
    }

    [Fact]
    public async Task Empty_ids_returns_empty_without_spawning()
    {
        var reader = new CommitHeaderReader(Cli, Path.GetTempPath());
        var headers = await reader.ReadAsync([]);
        Assert.Empty(headers);
    }
}
