namespace Historia.Tests;

/// <summary>§V2-8.2 / §V2-15.1: the additive OpenDiff contract on the state service.</summary>
public class RepositoryStateServiceTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo;

    public RepositoryStateServiceTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("repo-state-seed", b =>
        {
            b.WriteFile("a.txt", "one\n").Stage();
            b.Commit("init");
        });
    }

    private IRepositoryStateService NewState() => _host.Open(_repo.Path).State;

    private static DiffRequest Req(string path) =>
        new() { Source = DiffSource.UnstagedChanges, Path = path, ContextLines = 3 };

    [Fact]
    public void SetOpenDiff_raises_and_updates_current()
    {
        var state = NewState();
        OpenDiffChangedEventArgs? seen = null;
        state.OpenDiffChanged += (_, e) => seen = e;

        var r = Req("a.txt");
        state.SetOpenDiff(r);

        Assert.Equal(r, state.OpenDiff);
        Assert.NotNull(seen);
        Assert.Equal(r, seen!.Request);
    }

    [Fact]
    public void SetOpenDiff_is_idempotent_for_an_equal_request()
    {
        var state = NewState();
        state.SetOpenDiff(Req("a.txt"));

        var raises = 0;
        state.OpenDiffChanged += (_, _) => raises++;
        state.SetOpenDiff(Req("a.txt")); // value-equal record

        Assert.Equal(0, raises);
    }

    [Fact]
    public void RequestRevealCommit_raises_RevealCommitRequested()
    {
        var state = NewState();
        RevealCommitEventArgs? seen = null;
        state.RevealCommitRequested += (_, e) => seen = e;

        var id = new LibGit2Sharp.ObjectId("0123456789012345678901234567890123456789");
        state.RequestRevealCommit(id);

        Assert.NotNull(seen);
        Assert.Equal(id, seen!.CommitId);
    }

    [Fact]
    public void SetSelectedCommit_clears_OpenDiff_and_raises_null()
    {
        var state = NewState();
        state.SetOpenDiff(Req("a.txt"));

        OpenDiffChangedEventArgs? seen = null;
        state.OpenDiffChanged += (_, e) => seen = e;

        state.SetSelectedCommit(new LibGit2Sharp.ObjectId("0123456789abcdef0123456789abcdef01234567"));

        Assert.Null(state.OpenDiff);
        Assert.NotNull(seen);
        Assert.Null(seen!.Request);
    }

    [Fact]
    public void SetSelectedCommit_does_not_raise_OpenDiff_when_none_is_open()
    {
        var state = NewState();
        var raises = 0;
        state.OpenDiffChanged += (_, _) => raises++;

        state.SetSelectedCommit(new LibGit2Sharp.ObjectId("0123456789abcdef0123456789abcdef01234567"));

        Assert.Equal(0, raises);
    }

    [Fact]
    public void A_status_refresh_style_reselect_of_null_does_not_disturb_an_open_diff()
    {
        // SetSelectedCommit(null) when nothing is selected is a no-op and must not clear a diff.
        var state = NewState();
        state.SetOpenDiff(Req("a.txt"));
        state.SetSelectedCommit(null);
        Assert.NotNull(state.OpenDiff);
    }

    [Fact]
    public async Task RefreshAllAsync_saves_the_refreshed_refs_to_the_repo_state_cache()
    {
        await using var ctx = _host.Open(_repo.Path);
        var cache = new RecordingRepoStateCache();
        var state = new RepositoryStateService(ctx, ui: null, repoCache: cache);

        await state.RefreshAllAsync();

        Assert.Single(cache.SavedRefs);
        var (path, branches, _, _) = cache.SavedRefs[0];
        Assert.Equal(ctx.RepositoryPath, path);
        Assert.Contains(branches, b => b.IsCurrent);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _repo.Dispose();
    }

    private sealed class RecordingRepoStateCache : IRepoStateCache
    {
        public List<(string Path, IReadOnlyList<BranchInfo> Branches, IReadOnlyList<TagInfo> Tags, IReadOnlyList<RemoteInfo> Remotes)> SavedRefs { get; } = [];

        public RepoStateSnapshot? TryLoad(string repoPath) => null;
        public void SaveRefs(string repoPath, IReadOnlyList<BranchInfo> branches, IReadOnlyList<TagInfo> tags, IReadOnlyList<RemoteInfo> remotes)
            => SavedRefs.Add((repoPath, branches, tags, remotes));
        public void SavePowerTools(string repoPath, IReadOnlyList<StashEntry>? stashes, IReadOnlyList<SubmoduleInfo>? submodules, IReadOnlyList<WorktreeInfo>? worktrees) { }
        public int WipeAll() => 0;
    }
}
