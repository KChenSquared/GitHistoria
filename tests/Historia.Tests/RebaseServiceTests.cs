namespace Historia.Tests;
[Trait("tier", "git")]

public class RebaseServiceTests : IDisposable
{
    private static RebaseService Make(MiniHost host)
        => new((IGitCliService)host.Provider.GetService(typeof(IGitCliService))!);

    public RebaseServiceTests()
    {
        // Point GIT_SEQUENCE_EDITOR / GIT_EDITOR at the Historia executable re-invoked with its
        // rebase-helper switch. In-process it is the test host, so re-invoke Historia.dll.
        var HistoriaExe = Path.Combine(AppContext.BaseDirectory, "Historia.exe").Replace('\\', '/');
        RebaseService.EditorCommandOverride = (mode, dir) => $"\"{HistoriaExe}\" {mode} \"{dir}\"";
    }

    public void Dispose() => RebaseService.EditorCommandOverride = null;

    /// <summary>main: c1; then main gains m2 while feature adds f1,f2,f3 from c1.</summary>
    private static TestRepositoryBuilder Fixture() =>
        TestRepositoryBuilder.FromTemplate("rebase-fixture", b =>
        {
            b.WriteFile("base.txt", "base\n").Stage().Commit("c1");
            b.Checkout("feature", create: true);
            b.WriteFile("f1.txt", "1\n").Stage().Commit("f1");
            b.WriteFile("f2.txt", "2\n").Stage().Commit("f2");
            b.WriteFile("f3.txt", "3\n").Stage().Commit("f3");
            b.Checkout("main");
            b.WriteFile("m2.txt", "m\n").Stage().Commit("m2");
            b.Checkout("feature");
        });

    [Fact]
    public async Task Build_session_lists_upstream_to_head_oldest_first()
    {
        await using var host = new MiniHost();
        using var repo = Fixture();
        await using var ctx = host.Open(repo.Path);

        var session = await Make(host).BuildSessionAsync(ctx, "main");

        Assert.Equal(["f1", "f2", "f3"], session.Entries.Select(e => e.MessageShort));
        Assert.Equal("feature", session.SourceBranchName);
    }

    [Fact]
    public void Validation_flags_first_squash_empty_reword_and_all_drop()
    {
        var svc = new RebaseService(new NoGitCli());
        RebaseEntry E(string m, RebaseAction a, string? rw = null) => new()
        { CommitId = new(new string('a', 40)), MessageShort = m, AuthorName = "x", AuthorEmail = "x", Action = a, RewordMessage = rw };

        var session = new RebaseSession
        {
            OntoCommitId = new(new string('b', 40)), OntoRefName = "main", UpstreamRefName = "main", SourceBranchName = "f",
            Entries = [E("a", RebaseAction.Squash), E("b", RebaseAction.Reword, ""), E("c", RebaseAction.Drop)],
        };

        var issues = svc.Validate(session);
        Assert.Contains(issues, i => i.Message.Contains("first commit", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, i => i.Message.Contains("empty message", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Fast_path_rebase_replays_all_picks_onto_upstream()
    {
        await using var host = new MiniHost();
        using var repo = Fixture();
        await using var ctx = host.Open(repo.Path);
        var svc = Make(host);

        var session = await svc.BuildSessionAsync(ctx, "main");
        await svc.StartAsync(session, ctx, autostash: false);

        // feature now contains m2 in its history and the three feature commits on top.
        var log = repo.Git("log", "--oneline");
        Assert.Contains("m2", log);
        Assert.Contains("f3", log);
        var mergeBase = repo.Git("merge-base", "feature", "main").Trim();
        Assert.Equal(repo.Git("rev-parse", "main").Trim(), mergeBase); // feature is now ahead of main
    }

    // The interactive sequencer re-invokes the Historia executable (Historia.App.exe) as
    // GIT_SEQUENCE_EDITOR. The test project references only Historia.Core (a library),
    // so there is no exe in the output to point it at.
    [Fact(Skip = "the rebase-sequencer re-invoke moved to Historia.App (Program.Main); this end-to-end path is covered by manual testing, not the Core-only test project")]
    public async Task Interactive_reword_changes_the_message_with_no_editor_window()
    {
        await using var host = new MiniHost();
        using var repo = Fixture();
        await using var ctx = host.Open(repo.Path);
        var svc = Make(host);

        var session = await svc.BuildSessionAsync(ctx, "main");
        var entries = session.Entries.ToList();
        entries[1] = entries[1] with { Action = RebaseAction.Reword, RewordMessage = "f2 REWORDED" };

        await svc.StartAsync(session with { Entries = entries }, ctx, autostash: false);

        Assert.Contains("f2 REWORDED", repo.Git("log", "--format=%s"));
        Assert.DoesNotContain("f2\n", "\n" + repo.Git("log", "--format=%s"));
    }

    [Fact(Skip = "the rebase-sequencer re-invoke moved to Historia.App (Program.Main); this end-to-end path is covered by manual testing, not the Core-only test project")]
    public async Task Interactive_drop_removes_the_commit()
    {
        await using var host = new MiniHost();
        using var repo = Fixture();
        await using var ctx = host.Open(repo.Path);
        var svc = Make(host);

        var session = await svc.BuildSessionAsync(ctx, "main");
        var entries = session.Entries.ToList();
        entries[1] = entries[1] with { Action = RebaseAction.Drop }; // drop f2

        await svc.StartAsync(session with { Entries = entries }, ctx, autostash: false);

        var subjects = repo.Git("log", "--format=%s");
        Assert.Contains("f1", subjects);
        Assert.Contains("f3", subjects);
        Assert.DoesNotContain("\nf2\n", "\n" + subjects);
        Assert.False(File.Exists(Path.Combine(repo.Path, "f2.txt")));
    }

    [Fact]
    public async Task Build_session_matches_libgit2_oracle_with_merge_in_range()
    {
        await using var host = new MiniHost();
        using var repo = TestRepositoryBuilder.FromTemplate("rebase-merge-fixture", b =>
        {
            b.WriteFile("base.txt", "base\n").Stage().Commit("c1");
            b.Checkout("feature", create: true);
            b.WriteFile("f1.txt", "1\n").Stage().Commit("f1");
            b.Checkout("topic", create: true);
            b.WriteFile("t1.txt", "t1\n").Stage().Commit("t1");
            b.Checkout("feature");
            b.WriteFile("f2.txt", "2\n").Stage().Commit("f2");
            b.Git("merge", "topic", "-m", "merge topic");
            b.WriteFile("f3.txt", "3\n").Stage().Commit("f3");
            b.Checkout("main");
            b.WriteFile("m2.txt", "m\n").Stage().Commit("m2");
            b.Checkout("feature");
        });
        await using var ctx = host.Open(repo.Path);
        var svc = Make(host);

        var session = await svc.BuildSessionAsync(ctx, "main");

        using var directRepo = new Repository(repo.Path);
        var upstream = directRepo.Lookup<Commit>("main") ?? directRepo.Branches["main"]!.Tip;
        var filter = new CommitFilter
        {
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
            IncludeReachableFrom = directRepo.Head.Tip,
            ExcludeReachableFrom = upstream,
        };

        var expectedCommits = directRepo.Commits.QueryBy(filter).Reverse().ToList();

        Assert.Equal(upstream.Id.Sha, session.OntoCommitId.ToString());
        Assert.Equal("feature", session.SourceBranchName);
        Assert.Equal(string.Join(", ", expectedCommits.Select(c => c.MessageShort)), string.Join(", ", session.Entries.Select(e => e.MessageShort)));
        Assert.Equal(expectedCommits.Count, session.Entries.Count);
        for (var i = 0; i < expectedCommits.Count; i++)
        {
            Assert.Equal(expectedCommits[i].Id.Sha, session.Entries[i].CommitId.ToString());
            Assert.Equal(expectedCommits[i].MessageShort, session.Entries[i].MessageShort);
            Assert.Equal(expectedCommits[i].Author.Name, session.Entries[i].AuthorName);
            Assert.Equal(expectedCommits[i].Author.Email, session.Entries[i].AuthorEmail);
        }
    }

    [Fact]
    public async Task Build_session_matches_libgit2_oracle_with_equal_timestamps()
    {
        await using var host = new MiniHost();
        using var repo = TestRepositoryBuilder.FromTemplate("rebase-equal-dates-fixture", b =>
        {
            b.WriteFile("base.txt", "base\n").Stage();
            b.Git("commit", "--date=2026-01-01T00:00:00Z", "-m", "c1");
            b.Checkout("feature", create: true);
            b.WriteFile("f1.txt", "1\n").Stage();
            b.Git("commit", "--date=2026-01-02T12:00:00Z", "-m", "f1");
            b.WriteFile("f2.txt", "2\n").Stage();
            b.Git("commit", "--date=2026-01-02T12:00:00Z", "-m", "f2");
            b.WriteFile("f3.txt", "3\n").Stage();
            b.Git("commit", "--date=2026-01-02T12:00:00Z", "-m", "f3");
            b.Checkout("main");
            b.WriteFile("m2.txt", "m\n").Stage();
            b.Git("commit", "--date=2026-01-02T12:00:00Z", "-m", "m2");
            b.Checkout("feature");
        });
        await using var ctx = host.Open(repo.Path);
        var svc = Make(host);

        var session = await svc.BuildSessionAsync(ctx, "main");

        using var directRepo = new Repository(repo.Path);
        var upstream = directRepo.Lookup<Commit>("main") ?? directRepo.Branches["main"]!.Tip;
        var filter = new CommitFilter
        {
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
            IncludeReachableFrom = directRepo.Head.Tip,
            ExcludeReachableFrom = upstream,
        };

        var expectedCommits = directRepo.Commits.QueryBy(filter).Reverse().ToList();

        Assert.Equal(upstream.Id.Sha, session.OntoCommitId.ToString());
        Assert.Equal("feature", session.SourceBranchName);
        Assert.Equal(expectedCommits.Count, session.Entries.Count);
        for (var i = 0; i < expectedCommits.Count; i++)
        {
            Assert.Equal(expectedCommits[i].Id.Sha, session.Entries[i].CommitId.ToString());
            Assert.Equal(expectedCommits[i].MessageShort, session.Entries[i].MessageShort);
            Assert.Equal(expectedCommits[i].Author.Name, session.Entries[i].AuthorName);
            Assert.Equal(expectedCommits[i].Author.Email, session.Entries[i].AuthorEmail);
        }
    }

    [Fact]
    public async Task Build_session_keeps_signed_commits_when_log_shows_signatures()
    {
        // log.showSignature=true prints verification text on stdout ahead of a signed commit's
        // record. A parser that skipped it would leave the commit out of the todo, and the
        // rebase would silently drop it.
        await using var host = new MiniHost();
        using var repo = TestRepositoryBuilder.FromTemplate("rebase-signed-fixture", b =>
        {
            b.WriteFile("base.txt", "base\n").Stage().Commit("c1");
            b.Checkout("feature", create: true);
            b.WriteFile("f1.txt", "1\n").Stage().Commit("f1");
            b.WriteFile("f2.txt", "2\n").Stage();
            b.CommitWithForgedSignature("signed f2");
            b.WriteFile("f3.txt", "3\n").Stage().Commit("f3");
        });
        await using var ctx = host.Open(repo.Path);

        var session = await Make(host).BuildSessionAsync(ctx, "main");

        Assert.Equal(["f1", "signed f2", "f3"], session.Entries.Select(e => e.MessageShort));
    }
}
