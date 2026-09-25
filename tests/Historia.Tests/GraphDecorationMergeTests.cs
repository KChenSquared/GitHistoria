namespace Historia.Tests;

/// <summary>
/// §V3-7.3: a branch that exists locally and on one or more remotes folds into a single badge;
/// the remote prefix is stripped with <c>Branch.RemoteName</c> even when the remote name itself
/// contains a slash; a tag and a branch of the same name stay separate; HEAD folds into its
/// branch and only stands alone when detached.
/// </summary>
[Trait("tier", "git")]
public class GraphDecorationMergeTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    public GraphDecorationMergeTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("seed-decoration", b =>
        {
            b.WriteFile("a.txt", "1\n").Stage();
            b.Commit("c1");
        });
    }

    private async Task<GraphDecoration[]> DecorationsForTipAsync()
    {
        await using var ctx = _host.Open(_repo.Path);
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        var layout = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });
        return layout.Layout.Decorations.Where(d => d.RowIndex == 0).ToArray();
    }

    [Fact]
    public async Task Local_and_origin_fold_into_one_badge()
    {
        _repo.Git("remote", "add", "origin", _repo.Path);
        _repo.Git("update-ref", "refs/remotes/origin/main", "HEAD");

        var decos = await DecorationsForTipAsync();

        var main = Assert.Single(decos, d => d.Label == "main");
        Assert.True(main.IsLocal);
        Assert.Equal(new[] { "origin" }, main.RemotesOrNull);
        Assert.Equal(DecorationType.LocalBranch, main.Type);
    }

    [Fact]
    public async Task Slash_containing_remote_name_strips_the_prefix_correctly()
    {
        _repo.Git("remote", "add", "team/infra", _repo.Path);
        _repo.Git("update-ref", "refs/remotes/team/infra/main", "HEAD");

        var decos = await DecorationsForTipAsync();

        var main = Assert.Single(decos, d => d.Label == "main");
        Assert.Contains("team/infra", main.RemotesOrNull!);
        Assert.DoesNotContain(decos, d => d.Label.Contains('/'));
    }

    [Fact]
    public async Task A_tag_on_a_branch_stack_folds_into_the_group()
    {
        // §V3-B: two branches collapse, and a tag on that same commit folds in too — one badge,
        // no separate Tag decoration, the tag counted in the badge's +N, branches before tags.
        _repo.Git("branch", "dup", "HEAD");
        _repo.Git("tag", "v1", "HEAD");

        var decos = await DecorationsForTipAsync();

        Assert.DoesNotContain(decos, d => d.Type == DecorationType.Tag);
        var branch = Assert.Single(decos, d => d.Type == DecorationType.LocalBranch);
        var sibs = branch.SiblingsOrNull!;
        Assert.Contains(sibs, s => s.Label == "dup" && s.Type == DecorationType.LocalBranch);
        Assert.Contains(sibs, s => s.Label == "v1" && s.Type == DecorationType.Tag);
        // branches precede tags in the folded list
        Assert.Equal(DecorationType.Tag, sibs[^1].Type);
    }

    [Fact]
    public async Task A_tag_folds_into_a_lone_branch_on_the_commit()
    {
        // §V3-B: one branch is enough — the tag folds into main's badge, no separate Tag badge.
        _repo.Git("tag", "v1", "HEAD");

        var decos = await DecorationsForTipAsync();

        Assert.DoesNotContain(decos, d => d.Type == DecorationType.Tag);
        var main = Assert.Single(decos, d => d.Type == DecorationType.LocalBranch);
        Assert.Contains(main.SiblingsOrNull!, s => s.Label == "v1" && s.Type == DecorationType.Tag);
    }

    [Fact]
    public async Task An_annotated_tag_folds_into_a_lone_branch_on_the_commit()
    {
        _repo.Git("tag", "-a", "v1", "-m", "release 1", "HEAD");

        var decos = await DecorationsForTipAsync();

        Assert.DoesNotContain(decos, d => d.Type == DecorationType.Tag);
        var main = Assert.Single(decos, d => d.Type == DecorationType.LocalBranch);
        Assert.Contains(main.SiblingsOrNull!, s => s.Label == "v1" && s.Type == DecorationType.Tag);
    }

    [Fact]
    public async Task A_tag_on_a_commit_with_no_branch_keeps_its_own_badge()
    {
        _repo.WriteFile("a.txt", "2\n").Stage();
        _repo.Commit("c2");                // main advances; c1 now carries no branch
        _repo.Git("tag", "v1", "HEAD~1"); // tag the branch-less c1

        await using var ctx = _host.Open(_repo.Path);
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        var layout = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });
        var decos = layout.Layout.Decorations.ToArray();

        Assert.Single(decos, d => d.Label == "v1" && d.Type == DecorationType.Tag);
    }

    [Fact]
    public async Task An_annotated_tag_on_a_commit_keeps_its_own_badge()
    {
        _repo.WriteFile("a.txt", "2\n").Stage();
        _repo.Commit("c2");
        _repo.Git("tag", "-a", "v-annotated", "-m", "release note", "HEAD~1");

        await using var ctx = _host.Open(_repo.Path);
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        var layout = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });
        var decos = layout.Layout.Decorations.ToArray();

        Assert.Contains(decos, d => d.Label == "v-annotated" && d.Type == DecorationType.Tag);
    }

    [Fact]
    public async Task Remote_HEAD_pointer_is_not_a_badge()
    {
        _repo.Git("remote", "add", "origin", _repo.Path);
        _repo.Git("update-ref", "refs/remotes/origin/main", "HEAD");
        _repo.Git("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");

        var decos = await DecorationsForTipAsync();

        var main = Assert.Single(decos, d => d.Type == DecorationType.LocalBranch);
        Assert.DoesNotContain("HEAD", (main.SiblingsOrNull ?? []).Select(s => s.Label));
        Assert.DoesNotContain(decos, d => d.Label == "HEAD");
    }

    [Fact]
    public async Task Several_branches_on_one_commit_collapse_to_one_badge_default_first()
    {
        _repo.Git("checkout", "-b", "mybranch");   // HEAD → mybranch (the "default" by precedence a)
        _repo.Git("branch", "alpha", "HEAD");
        _repo.Git("branch", "zeta", "HEAD");

        var decos = await DecorationsForTipAsync();

        var badge = Assert.Single(decos, d => d.Type == DecorationType.LocalBranch);
        Assert.Equal("mybranch", badge.Label);                 // checked-out branch shown
        Assert.NotNull(badge.SiblingsOrNull);
        var labels = badge.SiblingsOrNull!.Select(s => s.Label).ToArray();
        Assert.Contains("alpha", labels);
        Assert.Contains("zeta", labels);
        // sibling branches are ordinal-sorted
        Assert.Equal(labels.OrderBy(x => x, StringComparer.Ordinal), labels);
    }

    [Fact]
    public async Task Checked_out_local_plus_same_name_remote_plus_another_branch_keeps_the_cloud(/* v8 M2 */)
    {
        // Repro from the todo: HEAD on a local branch, its origin tracking ref at the same
        // commit, and a second local branch there. The primary (checked-out) badge must still
        // carry the remote so the cloud glyph is drawn — it is not lost behind the +N counter.
        _repo.Git("remote", "add", "origin", _repo.Path);
        _repo.Git("update-ref", "refs/remotes/origin/main", "HEAD");
        _repo.Git("branch", "wip", "HEAD");

        var decos = await DecorationsForTipAsync();

        var primary = Assert.Single(decos, d => d.Type == DecorationType.LocalBranch);
        Assert.Equal("main", primary.Label);
        Assert.True(primary.IsHead);
        Assert.True(primary.IsLocal);
        Assert.Equal(new[] { "origin" }, primary.RemotesOrNull);         // cloud glyph source
        Assert.Contains(primary.SiblingsOrNull!, s => s.Label == "wip"); // the +N sibling
        Assert.DoesNotContain(primary.SiblingsOrNull!, s => s.Label == "main"); // origin/main folded in, not a sibling
    }

    [Fact]
    public async Task Head_folds_into_its_branch_when_attached()
    {
        var decos = await DecorationsForTipAsync();

        Assert.DoesNotContain(decos, d => d.Type == DecorationType.Head);
        var main = Assert.Single(decos, d => d.Label == "main");
        Assert.True(main.IsHead);
    }

    [Fact]
    public async Task Head_stands_alone_when_detached()
    {
        _repo.Git("checkout", "--detach", "HEAD");

        var decos = await DecorationsForTipAsync();

        var head = Assert.Single(decos, d => d.Type == DecorationType.Head);
        Assert.Equal("HEAD", head.Label);
        Assert.True(head.IsHead);
    }

    [Fact]
    public async Task Push_merges_remote_badge_on_recompute()
    {
        _repo.Git("remote", "add", "origin", _repo.Path);
        _repo.Git("update-ref", "refs/remotes/origin/main", "HEAD");

        _repo.WriteFile("a.txt", "2\n").Stage();
        _repo.Commit("c2");

        await using var ctx = _host.Open(_repo.Path);
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        var initial = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });

        // Tip initially has local main only (origin/main is at row 1)
        var tipDecosBefore = initial.Layout.Decorations.Where(d => d.RowIndex == 0).ToArray();
        var mainBefore = Assert.Single(tipDecosBefore, d => d.Label == "main");
        Assert.True(mainBefore.IsLocal);
        Assert.Null(mainBefore.RemotesOrNull);

        // Simulate push: origin/main updates to HEAD
        _repo.Git("update-ref", "refs/remotes/origin/main", "HEAD");

        // Recompute after refs change
        var recomputed = await svc.RecomputeAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs }, initial, InvalidationReason.RefsChanged);

        var tipDecosAfter = recomputed.Layout.Decorations.Where(d => d.RowIndex == 0).ToArray();
        var mainAfter = Assert.Single(tipDecosAfter, d => d.Label == "main");
        Assert.True(mainAfter.IsLocal);
        Assert.Equal(new[] { "origin" }, mainAfter.RemotesOrNull);
    }

    [Fact]
    public async Task Recompute_with_unchanged_refs_returns_previous_layout()
    {
        await using var ctx = _host.Open(_repo.Path);
        var svc = new GraphLayoutService(_host.Settings, _host.Locator);
        var initial = await svc.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });

        Assert.NotEqual(0, initial.Fingerprint);

        var recomputed = await svc.RecomputeAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs }, initial, InvalidationReason.RefsChanged);

        Assert.Same(initial, recomputed);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _repo.Dispose();
    }
}
