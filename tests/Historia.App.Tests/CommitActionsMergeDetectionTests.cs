namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public sealed class CommitActionsMergeDetectionTests
{
    [AvaloniaFact]
    public async Task Resident_merge_commit_resolves_mainline_1_without_gateway_call()
    {
        var mergeId = new GitObjectId("1111111111111111111111111111111111111111");
        var layout = CreateLayout(mergeId, parentCount: 2);

        var graph = new CommitGraphControl();
        graph.LayoutForTesting = layout;

        var gateway = new FakeGitGateway();
        graph.GatewayOverrideForTesting = gateway;

        var isMerge = await graph.IsMergeCommitAsync(ctx: null, mergeId, CancellationToken.None);
        var mainline = await graph.DetermineMainlineAsync(ctx: null, mergeId, CancellationToken.None);

        Assert.True(isMerge);
        Assert.Equal(1, mainline);
        Assert.Equal(0, gateway.GetCommitDetailCalls);
    }

    [AvaloniaFact]
    public async Task Resident_single_parent_commit_resolves_null_mainline_without_gateway_call()
    {
        var commitId = new GitObjectId("2222222222222222222222222222222222222222");
        var layout = CreateLayout(commitId, parentCount: 1);

        var graph = new CommitGraphControl();
        graph.LayoutForTesting = layout;

        var gateway = new FakeGitGateway();
        graph.GatewayOverrideForTesting = gateway;

        var isMerge = await graph.IsMergeCommitAsync(ctx: null, commitId, CancellationToken.None);
        var mainline = await graph.DetermineMainlineAsync(ctx: null, commitId, CancellationToken.None);

        Assert.False(isMerge);
        Assert.Null(mainline);
        Assert.Equal(0, gateway.GetCommitDetailCalls);
    }

    [AvaloniaFact]
    public async Task Resident_root_commit_resolves_null_mainline_without_gateway_call()
    {
        var rootId = new GitObjectId("3333333333333333333333333333333333333333");
        var layout = CreateLayout(rootId, parentCount: 0);

        var graph = new CommitGraphControl();
        graph.LayoutForTesting = layout;

        var gateway = new FakeGitGateway();
        graph.GatewayOverrideForTesting = gateway;

        var isMerge = await graph.IsMergeCommitAsync(ctx: null, rootId, CancellationToken.None);
        var mainline = await graph.DetermineMainlineAsync(ctx: null, rootId, CancellationToken.None);

        Assert.False(isMerge);
        Assert.Null(mainline);
        Assert.Equal(0, gateway.GetCommitDetailCalls);
    }

    [AvaloniaFact]
    public async Task Non_resident_merge_commit_consults_gateway_once()
    {
        var nonResidentMergeId = new GitObjectId("4444444444444444444444444444444444444444");
        var parent1 = new GitObjectId("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var parent2 = new GitObjectId("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        var graph = new CommitGraphControl();
        graph.LayoutForTesting = null;

        var gateway = new FakeGitGateway
        {
            OnGetCommitDetail = id => CreateCommitDetail(id, [parent1, parent2]),
        };
        graph.GatewayOverrideForTesting = gateway;

        var isMerge = await graph.IsMergeCommitAsync(ctx: null, nonResidentMergeId, CancellationToken.None);
        Assert.True(isMerge);
        Assert.Equal(1, gateway.GetCommitDetailCalls);

        var mainline = await graph.DetermineMainlineAsync(ctx: null, nonResidentMergeId, CancellationToken.None);
        Assert.Equal(1, mainline);
        Assert.Equal(2, gateway.GetCommitDetailCalls);
    }

    [AvaloniaFact]
    public async Task Non_resident_single_parent_commit_consults_gateway_once()
    {
        var nonResidentSingleId = new GitObjectId("5555555555555555555555555555555555555555");
        var parent1 = new GitObjectId("cccccccccccccccccccccccccccccccccccccccc");

        var graph = new CommitGraphControl();
        graph.LayoutForTesting = null;

        var gateway = new FakeGitGateway
        {
            OnGetCommitDetail = id => CreateCommitDetail(id, [parent1]),
        };
        graph.GatewayOverrideForTesting = gateway;

        var isMerge = await graph.IsMergeCommitAsync(ctx: null, nonResidentSingleId, CancellationToken.None);
        Assert.False(isMerge);
        Assert.Equal(1, gateway.GetCommitDetailCalls);

        var mainline = await graph.DetermineMainlineAsync(ctx: null, nonResidentSingleId, CancellationToken.None);
        Assert.Null(mainline);
        Assert.Equal(2, gateway.GetCommitDetailCalls);
    }

    [AvaloniaFact]
    public async Task Non_resident_root_commit_consults_gateway_once()
    {
        var nonResidentRootId = new GitObjectId("6666666666666666666666666666666666666666");

        var graph = new CommitGraphControl();
        graph.LayoutForTesting = null;

        var gateway = new FakeGitGateway
        {
            OnGetCommitDetail = id => CreateCommitDetail(id, []),
        };
        graph.GatewayOverrideForTesting = gateway;

        var isMerge = await graph.IsMergeCommitAsync(ctx: null, nonResidentRootId, CancellationToken.None);
        Assert.False(isMerge);
        Assert.Equal(1, gateway.GetCommitDetailCalls);

        var mainline = await graph.DetermineMainlineAsync(ctx: null, nonResidentRootId, CancellationToken.None);
        Assert.Null(mainline);
        Assert.Equal(2, gateway.GetCommitDetailCalls);
    }

    private static GraphLayoutResult CreateLayout(GitObjectId id, int parentCount)
    {
        var parents = new GitObjectId[parentCount];
        for (var i = 0; i < parentCount; i++)
            parents[i] = new GitObjectId(new string((char)('a' + i), 40));

        var metadata = new CommitMetadata
        {
            Id = id,
            MessageShort = "Subject",
            AuthorName = "Dev",
            AuthorEmail = "dev@example.com",
            AuthorWhen = DateTimeOffset.UtcNow,
            CommitWhen = DateTimeOffset.UtcNow,
            ParentCount = parentCount,
        };

        var raw = new RawCommit(id, parents, metadata);
        return new DagLayoutEngine().Compute([raw], null, maxRows: 1);
    }

    private static CommitDetail CreateCommitDetail(GitObjectId id, IReadOnlyList<GitObjectId> parentIds)
    {
        return new CommitDetail
        {
            Id = id,
            MessageShort = "Subject",
            MessageBody = "",
            AuthorName = "Dev",
            AuthorEmail = "dev@example.com",
            AuthorWhen = DateTimeOffset.UtcNow,
            CommitterName = "Dev",
            CommitterEmail = "dev@example.com",
            CommitWhen = DateTimeOffset.UtcNow,
            ParentIds = parentIds,
        };
    }

    private sealed class FakeGitGateway : IGitGateway
    {
        public string RepositoryPath => "C:\\fake\\repo";
        public int GetCommitDetailCalls;
        public Func<GitObjectId, CommitDetail>? OnGetCommitDetail { get; set; }

        public Task<CommitDetail> GetCommitDetailAsync(GitObjectId id, CancellationToken ct = default)
        {
            Interlocked.Increment(ref GetCommitDetailCalls);
            if (OnGetCommitDetail != null)
                return Task.FromResult(OnGetCommitDetail(id));

            return Task.FromResult(CreateCommitDetail(id, []));
        }

        public Task<RepositoryInfo> GetRepositoryInfoAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Historia.Models.RepositoryStatus> GetStatusAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<FileStatusEntry>> GetCommitFilesAsync(GitObjectId id, int parentIndex, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<BranchInfo>> GetBranchesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemoteInfo>> GetRemotesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<FileDiff> GetFileDiffAsync(DiffRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<AheadBehind?> GetDivergenceAsync(string local, string remote, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string? Name, string? Email)> GetConfiguredIdentityAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string? Name, string? Email)> GetGlobalIdentityAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> GetGpgSignConfiguredAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StageAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DiscardAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitObjectId> CommitAsync(CommitRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task MergeAsync(string refish, bool noFastForward, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ResetAsync(string target, GitResetMode mode, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CountCommitsLeavingAsync(string target, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CheckoutAsync(string refName, GitCheckoutOptions options, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BranchInfo> CreateBranchAsync(string name, string startPoint, bool checkout, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteBranchAsync(string name, bool force, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RenameBranchAsync(string oldName, string newName, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetUpstreamAsync(string branchName, string upstreamRef, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TagInfo> CreateTagAsync(string name, string target, string? message, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteTagAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AddRemoteAsync(string name, string url, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RemoveRemoteAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetRemoteUrlAsync(string name, string url, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> RenameRemoteAsync(string oldName, string newName, CancellationToken ct = default) => throw new NotImplementedException();
        public Task FetchAsync(string remote, GitFetchOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
        public Task PullAsync(string remote, string branch, GitPullOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
        public Task PushAsync(string remote, PushSpec spec, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
