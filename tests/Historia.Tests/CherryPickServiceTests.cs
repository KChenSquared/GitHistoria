namespace Historia.Tests;
[Trait("tier", "git")]

public class CherryPickServiceTests
{
    private static CherryPickService Make(MiniHost host)
        => new((IGitCliService)host.Provider.GetService(typeof(IGitCliService))!);

    /// <summary>main has c1; feature branches from c1 with a,b,c. Returns (repo, shas of a,b,c).</summary>
    private static (TestRepositoryBuilder Repo, string A, string B, string C) Fixture()
    {
        var b = TestRepositoryBuilder.FromTemplate("cherry-pick-fixture", r =>
        {
            r.WriteFile("base.txt", "base\n").Stage().Commit("c1");
            r.Checkout("feature", create: true);
            r.WriteFile("a.txt", "a\n").Stage().Commit("add a");
            r.WriteFile("b.txt", "b\n").Stage().Commit("add b");
            r.WriteFile("c.txt", "c\n").Stage().Commit("add c");
            r.Checkout("main");
        });
        var a = b.Git("rev-parse", "feature~2").Trim();
        var bb = b.Git("rev-parse", "feature~1").Trim();
        var c = b.Git("rev-parse", "feature").Trim();
        return (b, a, bb, c);
    }

    [Fact]
    public async Task Cherry_pick_single_commit_applies_it_to_the_current_branch()
    {
        await using var host = new MiniHost();
        var (repo, a, _, _) = Fixture();
        await using var ctx = host.Open(repo.Path);

        await Make(host).CherryPickAsync(ctx, [new LibGit2Sharp.ObjectId(a)]);

        Assert.True(File.Exists(Path.Combine(repo.Path, "a.txt")));
        Assert.Contains("add a", repo.Git("log", "--oneline", "-1"));
    }

    [Fact]
    public async Task Cherry_pick_a_list_applies_oldest_first()
    {
        await using var host = new MiniHost();
        var (repo, a, bb, c) = Fixture();
        await using var ctx = host.Open(repo.Path);

        await Make(host).CherryPickAsync(ctx, [new(a), new(bb), new(c)]);

        var log = repo.Git("log", "--oneline", "-3");
        var lines = log.Trim().Split('\n');
        Assert.Contains("add c", lines[0]);   // newest last-applied on top
        Assert.Contains("add b", lines[1]);
        Assert.Contains("add a", lines[2]);
    }

    [Fact]
    public async Task Revert_undoes_a_commit()
    {
        await using var host = new MiniHost();
        using var repo = TestRepositoryBuilder.FromTemplate("revert-seed", r =>
        {
            r.WriteFile("f.txt", "keep\nremove-me\n").Stage().Commit("c1");
            r.WriteFile("f.txt", "keep\n").Stage().Commit("delete a line");
        });
        var bad = repo.HeadSha;
        await using var ctx = host.Open(repo.Path);

        await Make(host).RevertAsync(ctx, [new LibGit2Sharp.ObjectId(bad)]);

        Assert.Contains("remove-me", (await File.ReadAllTextAsync(Path.Combine(repo.Path, "f.txt"))));
    }

    [Fact]
    public async Task Undo_to_orig_head_restores_pre_cherry_pick_state()
    {
        await using var host = new MiniHost();
        var (repo, a, _, _) = Fixture();
        await using var ctx = host.Open(repo.Path);
        var svc = Make(host);
        var before = repo.Git("rev-parse", "HEAD").Trim();

        await svc.CherryPickAsync(ctx, [new LibGit2Sharp.ObjectId(a)]);
        Assert.NotEqual(before, repo.Git("rev-parse", "HEAD").Trim());

        await svc.UndoToOrigHeadAsync(ctx);
        Assert.Equal(before, repo.Git("rev-parse", "HEAD").Trim());
        Assert.False(File.Exists(Path.Combine(repo.Path, "a.txt")));
    }
}
