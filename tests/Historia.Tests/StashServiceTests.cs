namespace Historia.Tests;
[Trait("tier", "git")]

public class StashServiceTests
{
    private static StashService Make(MiniHost host)
        => new((IGitCliService)host.Provider.GetService(typeof(IGitCliService))!);

    private static TestRepositoryBuilder SeededRepo() =>
        TestRepositoryBuilder.FromTemplate("stash-seed", b => b.WriteFile("f.txt", "base\n").Stage().Commit("c1"));

    private static string Read(TestRepositoryBuilder b)
        => File.ReadAllText(Path.Combine(b.Path, "f.txt")).Replace("\r\n", "\n");

    [Fact]
    public async Task Save_then_list_then_pop_round_trips()
    {
        await using var host = new MiniHost();
        using var b = SeededRepo();
        await using var ctx = host.Open(b.Path);
        var svc = Make(host);

        b.WriteFile("f.txt", "dirty\n");
        Assert.True(await svc.SaveAsync(ctx, new StashSaveOptions { Message = "wip" }));

        var list = await svc.ListAsync(ctx);
        var entry = Assert.Single(list);
        Assert.Contains("wip", entry.Message);
        Assert.Equal("base\n", Read(b));

        await svc.ApplyAndDropAsync(ctx, entry.CommitId);
        Assert.Equal("dirty\n", Read(b));
        Assert.Empty(await svc.ListAsync(ctx));
    }

    [Fact]
    public async Task List_is_empty_without_refs_stash_but_other_failures_throw()
    {
        await using var host = new MiniHost();
        using var b = SeededRepo();
        await using (var clean = host.Open(b.Path))
            Assert.Empty(await Make(host).ListAsync(clean));

        // With refs/stash present, a failing `log -g` is a real error, not "no stashes".
        b.WriteFile("f.txt", "dirty\n");
        b.Git("stash", "push", "-m", "wip");
        var cli = new InterceptingGitCliService(host.Cli)
        {
            Intercept = r => r.Arguments.Contains("-g") ? InterceptingGitCliService.Failure() : null,
        };
        await using var ctx = host.Open(b.Path, cli);

        await Assert.ThrowsAsync<HistoriaGitException>(() => new StashService(cli).ListAsync(ctx));
    }

    [Fact]
    public async Task Save_reports_false_when_nothing_to_stash()
    {
        await using var host = new MiniHost();
        using var b = SeededRepo();
        await using var ctx = host.Open(b.Path);
        Assert.False(await Make(host).SaveAsync(ctx, new StashSaveOptions()));
    }

    [Fact]
    public async Task StashStaged_stashes_only_the_staged_content()
    {
        await using var host = new MiniHost();
        var b = TestRepositoryBuilder.FromTemplate("stash-staged-seed", r =>
            r.WriteFile("a.txt", "a base\n").WriteFile("b.txt", "b base\n").Stage().Commit("c1"));
        using (b)
        {
            await using var ctx = host.Open(b.Path);
            var svc = Make(host);

            b.WriteFile("a.txt", "a staged\n").Stage("a.txt"); // staged
            b.WriteFile("b.txt", "b working\n");               // left unstaged

            Assert.True(await svc.StashStagedAsync(ctx, "only-staged"));

            // The staged edit is gone from the working tree; the unstaged edit stays.
            Assert.Equal("a base\n", File.ReadAllText(Path.Combine(b.Path, "a.txt")).Replace("\r\n", "\n"));
            Assert.Equal("b working\n", File.ReadAllText(Path.Combine(b.Path, "b.txt")).Replace("\r\n", "\n"));

            var entry = Assert.Single(await svc.ListAsync(ctx));
            Assert.Contains("only-staged", entry.Message);

            // Applying it cleanly restores the staged change on top of the still-dirty b.txt.
            await svc.ApplyAndDropAsync(ctx, entry.CommitId);
            Assert.Equal("a staged\n", File.ReadAllText(Path.Combine(b.Path, "a.txt")).Replace("\r\n", "\n"));
            Assert.Equal("b working\n", File.ReadAllText(Path.Combine(b.Path, "b.txt")).Replace("\r\n", "\n"));
        }
    }

    [Fact]
    public async Task StashStaged_reports_false_when_nothing_is_staged()
    {
        await using var host = new MiniHost();
        using var b = SeededRepo();
        await using var ctx = host.Open(b.Path);
        b.WriteFile("f.txt", "dirty-unstaged\n"); // modified but not staged
        Assert.False(await Make(host).StashStagedAsync(ctx, null));
        Assert.Equal("dirty-unstaged\n", Read(b));
    }

    [Fact]
    public async Task Operations_are_keyed_by_commit_id_when_the_stack_shifts()
    {
        await using var host = new MiniHost();
        using var b = SeededRepo();
        await using var ctx = host.Open(b.Path);
        var svc = Make(host);

        b.WriteFile("f.txt", "one\n"); await svc.SaveAsync(ctx, new StashSaveOptions { Message = "first" });
        b.WriteFile("f.txt", "two\n"); await svc.SaveAsync(ctx, new StashSaveOptions { Message = "second" });

        var list = await svc.ListAsync(ctx);           // stash@{0}=second, stash@{1}=first
        var first = list.Single(e => e.Message.Contains("first"));
        Assert.Equal(1, first.Index);

        var second = list.Single(e => e.Message.Contains("second"));
        await svc.DropAsync(ctx, second.CommitId);
        await svc.DropAsync(ctx, first.CommitId);      // its index is now 0, resolved fresh

        Assert.Empty(await svc.ListAsync(ctx));
    }

    [Fact]
    public async Task Acting_on_a_stale_commit_id_fails_cleanly()
    {
        await using var host = new MiniHost();
        using var b = SeededRepo();
        await using var ctx = host.Open(b.Path);
        var svc = Make(host);

        b.WriteFile("f.txt", "x\n");
        await svc.SaveAsync(ctx, new StashSaveOptions { Message = "s" });
        var entry = (await svc.ListAsync(ctx)).Single();
        await svc.DropAsync(ctx, entry.CommitId);

        await Assert.ThrowsAsync<HistoriaGitException>(() => svc.ApplyAsync(ctx, entry.CommitId, false));
    }

    [Fact]
    public async Task Branch_from_stash_creates_the_branch_and_drops_the_entry()
    {
        await using var host = new MiniHost();
        using var b = SeededRepo();
        await using var ctx = host.Open(b.Path);
        var svc = Make(host);

        b.WriteFile("f.txt", "on-branch\n");
        await svc.SaveAsync(ctx, new StashSaveOptions { Message = "s" });
        var entry = (await svc.ListAsync(ctx)).Single();

        await svc.BranchFromAsync(ctx, entry.CommitId, "from-stash");

        Assert.Contains("from-stash", b.Git("branch", "--list", "from-stash"));
        Assert.Empty(await svc.ListAsync(ctx));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("untracked")]
    [InlineData("index-only")]
    [InlineData("colon-message")]
    public async Task Stash_list_matches_libgit2_direct_oracle(string scenario)
    {
        await using var host = new MiniHost();
        using var b = TestRepositoryBuilder.FromTemplate($"stash-parity-{scenario}", r =>
            r.WriteFile("f.txt", "base\n").Stage().Commit("c1"));
        await using var ctx = host.Open(b.Path);
        var svc = Make(host);

        switch (scenario)
        {
            case "plain":
                b.WriteFile("f.txt", "plain dirty\n");
                await svc.SaveAsync(ctx, new StashSaveOptions { Message = "plain stash" });
                break;

            case "untracked":
                b.WriteFile("f.txt", "tracked dirty\n");
                b.WriteFile("untracked.txt", "untracked dirty\n");
                await svc.SaveAsync(ctx, new StashSaveOptions { Message = "with untracked", IncludeUntracked = true });
                break;

            case "index-only":
                b.WriteFile("f.txt", "index dirty\n").Stage("f.txt");
                await svc.StashStagedAsync(ctx, "index only stash");
                break;

            case "colon-message":
                b.WriteFile("f.txt", "colon dirty\n");
                await svc.SaveAsync(ctx, new StashSaveOptions { Message = "WIP on main: fix: colons: everywhere" });
                break;
        }

        var cliEntries = await svc.ListAsync(ctx);

        using var directRepo = new Repository(b.Path);
        var directEntries = directRepo.Stashes.Select((stash, idx) =>
        {
            var commit = stash.WorkTree;
            var parent = commit.Parents.FirstOrDefault();
            var fileCount = 0;
            if (parent is not null)
            {
                using var changes = directRepo.Diff.Compare<TreeChanges>(parent.Tree, commit.Tree);
                fileCount = changes.Count();
            }

            return new
            {
                Index = idx,
                CommitId = commit.Id.Sha,
                Message = stash.Message,
                AuthorName = commit.Author.Name,
                AuthorEmail = commit.Author.Email,
                IncludesUntracked = commit.Parents.Count() == 3,
                FileCount = fileCount,
            };
        }).ToList();

        Assert.NotEmpty(cliEntries);
        Assert.Equal(directEntries.Count, cliEntries.Count);
        for (var i = 0; i < cliEntries.Count; i++)
        {
            var cli = cliEntries[i];
            var direct = directEntries[i];

            Assert.Equal(direct.Index, cli.Index);
            Assert.Equal(direct.CommitId, cli.CommitId.ToString());
            Assert.Equal(direct.Message, cli.Message);
            Assert.Equal(direct.AuthorName, cli.AuthorName);
            Assert.Equal(direct.AuthorEmail, cli.AuthorEmail);
            Assert.Equal(direct.IncludesUntracked, cli.IncludesUntracked);
            Assert.Equal(direct.FileCount, cli.FileCount);
        }
    }
}
