namespace Historia.Tests;
[Trait("tier", "git")]

/// <summary>Mutating commands that update the work tree, run hooks or drive a sequencer must not
/// carry a wall-clock timeout: killing git midway leaves a half-applied tree and a stale
/// index.lock. Only the caller's token may cancel them.</summary>
public class MutatingCommandTimeoutTests
{
    private static IGitCliService RealCli(MiniHost host) => host.Cli;

    private static string GitDir(TestRepositoryBuilder repo) => Path.Combine(repo.Path, ".git");

    private static void AssertNoWallClock(InterceptingGitCliService cli, string verb, string? subVerb = null)
    {
        var matching = cli.Requests
            .Where(r => r.Arguments.Count > 0 && r.Arguments[0] == verb
                        && (subVerb is null || (r.Arguments.Count > 1 && r.Arguments[1] == subVerb)))
            .ToList();
        Assert.NotEmpty(matching);
        foreach (var request in matching)
            Assert.True(request.Timeout == Timeout.InfiniteTimeSpan,
                $"`git {string.Join(' ', request.Arguments)}` has a finite timeout ({request.Timeout}).");
    }

    [Fact]
    public async Task CherryPick_sequence_has_no_wall_clock_timeout()
    {
        await using var host = new MiniHost();
        using var repo = TestRepositoryBuilder.FromTemplate("mutating-timeout-cherry-pick", r =>
        {
            r.WriteFile("base.txt", "base\n").Stage().Commit("c1");
            r.Checkout("feature", create: true);
            r.WriteFile("a.txt", "a\n").Stage().Commit("add a");
            r.WriteFile("b.txt", "b\n").Stage().Commit("add b");
            r.Checkout("main");
        });
        var a = GitObjectId.Parse(repo.Git("rev-parse", "feature~1").Trim());
        var b = GitObjectId.Parse(repo.Git("rev-parse", "feature").Trim());
        var cli = new InterceptingGitCliService(RealCli(host));
        await using var ctx = host.Open(repo.Path, cli);
        var svc = new CherryPickService(cli);

        await svc.CherryPickAsync(ctx, [a, b]);
        Assert.True(File.Exists(Path.Combine(repo.Path, "b.txt")));
        AssertNoWallClock(cli, "cherry-pick");

        await svc.UndoToOrigHeadAsync(ctx);
        Assert.False(File.Exists(Path.Combine(repo.Path, "a.txt")));
        AssertNoWallClock(cli, "reset");
    }

    [Fact]
    public async Task Revert_has_no_wall_clock_timeout()
    {
        await using var host = new MiniHost();
        using var repo = TestRepositoryBuilder.FromTemplate("mutating-timeout-revert", r =>
        {
            r.WriteFile("f.txt", "keep\nremove-me\n").Stage().Commit("c1");
            r.WriteFile("f.txt", "keep\n").Stage().Commit("delete a line");
        });
        var bad = GitObjectId.Parse(repo.HeadSha);
        var cli = new InterceptingGitCliService(RealCli(host));
        await using var ctx = host.Open(repo.Path, cli);

        await new CherryPickService(cli).RevertAsync(ctx, [bad]);

        Assert.Contains("remove-me", await File.ReadAllTextAsync(Path.Combine(repo.Path, "f.txt")));
        AssertNoWallClock(cli, "revert");
    }

    [Fact]
    public async Task Stash_push_and_pop_have_no_wall_clock_timeout()
    {
        await using var host = new MiniHost();
        using var repo = TestRepositoryBuilder.FromTemplate("mutating-timeout-stash", r =>
            r.WriteFile("f.txt", "base\n").WriteFile("g.txt", "base\n").Stage().Commit("c1"));
        var cli = new InterceptingGitCliService(RealCli(host));
        await using var ctx = host.Open(repo.Path, cli);
        var svc = new StashService(cli);

        repo.WriteFile("f.txt", "dirty\n");
        Assert.True(await svc.SaveAsync(ctx, new StashSaveOptions { Message = "wip" }));
        AssertNoWallClock(cli, "stash", "push");

        repo.WriteFile("g.txt", "staged\n").Stage("g.txt");
        Assert.True(await svc.StashStagedAsync(ctx, "staged-only"));
        Assert.Contains(cli.Requests, r => r.Arguments.Contains("--staged"));
        Assert.All(
            cli.Requests.Where(r => r.Arguments.Count > 1 && r.Arguments[0] == "stash" && r.Arguments[1] == "push"),
            r => Assert.Equal(Timeout.InfiniteTimeSpan, r.Timeout));

        var entries = await svc.ListAsync(ctx);
        await svc.ApplyAndDropAsync(ctx, entries[0].CommitId);
        AssertNoWallClock(cli, "stash", "pop");
    }

    [Fact]
    public async Task Conflict_continue_has_no_wall_clock_timeout()
    {
        await using var host = new MiniHost();
        using var repo = TestRepositoryBuilder.FromTemplate("mutating-timeout-conflict", r =>
        {
            r.WriteFile("f.txt", "line1\nshared\nline3\n").Stage().Commit("base");
            r.Checkout("feature", create: true).WriteFile("f.txt", "line1\nTHEIRS\nline3\n").Stage().Commit("theirs");
            r.Checkout("main").WriteFile("f.txt", "line1\nOURS\nline3\n").Stage().Commit("ours");
        });
        var theirs = GitObjectId.Parse(repo.Git("rev-parse", "feature").Trim());
        var cli = new InterceptingGitCliService(RealCli(host));
        await using var ctx = host.Open(repo.Path, cli);

        // Started with the CLI directly: the sequence itself is covered above, and this test drives the
        // conflict-resolution requests.
        try { repo.Git("cherry-pick", theirs.ToString()); } catch (InvalidOperationException) { /* stops on the conflict */ }
        Assert.True(File.Exists(Path.Combine(GitDir(repo), "CHERRY_PICK_HEAD")));

        var svc = new ConflictService(cli, host.Settings);
        // Taking theirs keeps the resolved tree different from HEAD; an empty result would make
        // --continue refuse.
        await svc.ResolveWithSideAsync(ctx, "f.txt", ConflictSide.Theirs);
        AssertNoWallClock(cli, "checkout");
        AssertNoWallClock(cli, "add");

        await svc.ContinueOperationAsync(ctx);
        Assert.False(File.Exists(Path.Combine(GitDir(repo), "CHERRY_PICK_HEAD")));
        AssertNoWallClock(cli, "cherry-pick");
    }

    [Fact]
    public async Task Rebase_start_has_no_wall_clock_timeout()
    {
        await using var host = new MiniHost();
        using var repo = TestRepositoryBuilder.FromTemplate("mutating-timeout-rebase", b =>
        {
            b.WriteFile("base.txt", "base\n").Stage().Commit("c1");
            b.Checkout("feature", create: true);
            b.WriteFile("f1.txt", "1\n").Stage().Commit("f1");
            b.Checkout("main");
            b.WriteFile("m2.txt", "m\n").Stage().Commit("m2");
            b.Checkout("feature");
        });
        var cli = new InterceptingGitCliService(RealCli(host));
        await using var ctx = host.Open(repo.Path, cli);
        var svc = new RebaseService(cli);

        var session = await svc.BuildSessionAsync(ctx, "main");
        await svc.StartAsync(session, ctx, autostash: false);

        Assert.Contains("m2", repo.Git("log", "--oneline"));
        AssertNoWallClock(cli, "rebase");
    }
}
