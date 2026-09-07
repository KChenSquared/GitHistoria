namespace Historia.Tests;

public class StashServiceTests
{
    private static StashService Make(MiniHost host)
        => new((IGitCliService)host.Provider.GetService(typeof(IGitCliService))!);

    private static TestRepositoryBuilder SeededRepo()
    {
        var b = TestRepositoryBuilder.Create();
        b.WriteFile("f.txt", "base\n").Stage().Commit("c1");
        return b;
    }

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
    public async Task Save_reports_false_when_nothing_to_stash()
    {
        await using var host = new MiniHost();
        using var b = SeededRepo();
        await using var ctx = host.Open(b.Path);
        Assert.False(await Make(host).SaveAsync(ctx, new StashSaveOptions()));
    }

    [Fact]
    public async Task StashStaged_stashes_only_the_staged_content(/* v8 M15 */)
    {
        await using var host = new MiniHost();
        var b = TestRepositoryBuilder.Create();
        b.WriteFile("a.txt", "a base\n").WriteFile("b.txt", "b base\n").Stage().Commit("c1");
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
}
