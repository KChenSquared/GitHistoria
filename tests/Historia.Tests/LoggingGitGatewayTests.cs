using Historia.Diagnostics;
using Historia.Services.Git;

namespace Historia.Tests;
[Trait("tier", "unit")]

public sealed class LoggingGitGatewayTests : IDisposable
{
    private readonly StateDir _dir = new();

    public LoggingGitGatewayTests()
    {
        AppLog.Enabled = false;
        AppLog.ResetForTesting();
    }

    public void Dispose()
    {
        AppLog.Enabled = false;
        AppLog.ResetForTesting();
        _dir.Dispose();
    }

    /// <summary>Disables logging first — the session writer now holds the file open for the
    /// whole session (§findings: no more open/close per line), so the handle must be closed
    /// before a second reader can open the same file.</summary>
    private string SessionText()
    {
        AppLog.Enabled = false;
        return File.ReadAllText(Directory.GetFiles(AppPaths.LogsDirectory, "dev-*.log").Single());
    }

    [Fact]
    public async Task Delegates_and_returns_the_inner_result()
    {
        var inner = new FakeGateway
        {
            Branches = [Branch("main"), Branch("dev")],
        };
        var gw = new LoggingGitGateway(inner, @"C:\repos\demo");

        var result = await gw.GetBranchesAsync();

        Assert.Equal(2, result.Count);
        Assert.Equal(1, inner.GetBranchesCalls);
    }

    [Fact]
    public async Task Propagates_exceptions_unchanged()
    {
        var boom = new InvalidOperationException("detached HEAD");
        var gw = new LoggingGitGateway(new FakeGateway { CheckoutThrows = boom }, @"C:\repos\demo");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gw.CheckoutAsync("main", new GitCheckoutOptions()));
        Assert.Same(boom, thrown);
    }

    [Fact]
    public async Task Logs_the_operation_with_arguments_and_outcome()
    {
        AppLog.Enabled = true;
        var gw = new LoggingGitGateway(new FakeGateway { Branches = [Branch("main")] }, @"C:\repos\demo");

        await gw.GetBranchesAsync();

        var text = SessionText();
        Assert.Contains("[demo] GetBranchesAsync", text);
        Assert.Contains("1 branches", text);
    }

    [Fact]
    public async Task Logs_a_failed_operation()
    {
        AppLog.Enabled = true;
        var gw = new LoggingGitGateway(
            new FakeGateway { CheckoutThrows = new InvalidOperationException("nope") }, @"C:\repos\demo");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gw.CheckoutAsync("main", new GitCheckoutOptions()));

        var text = SessionText();
        Assert.Contains("CheckoutAsync(main)", text);
        Assert.Contains("FAILED: InvalidOperationException: nope", text);
    }

    [Fact]
    public async Task CountCommitsLeavingAsync_delegates_and_logs_the_count()
    {
        AppLog.Enabled = true;
        var gw = new LoggingGitGateway(new FakeGateway { CountCommitsLeavingResult = 3 }, @"C:\repos\demo");

        var n = await gw.CountCommitsLeavingAsync("origin/main");

        Assert.Equal(3, n);
        var text = SessionText();
        Assert.Contains("CountCommitsLeavingAsync(origin/main)", text);
        Assert.Contains("(3,", text);
    }

    [Fact]
    public async Task Pure_passthrough_when_logging_is_disabled()
    {
        var inner = new FakeGateway { Branches = [Branch("main")] };
        var gw = new LoggingGitGateway(inner, @"C:\repos\demo");

        await gw.GetBranchesAsync();

        Assert.False(Directory.Exists(AppPaths.LogsDirectory)
            && Directory.GetFiles(AppPaths.LogsDirectory, "dev-*.log").Length > 0);
    }

    private static BranchInfo Branch(string name) => new()
    {
        Name = name,
        CanonicalName = "refs/heads/" + name,
        IsRemote = false,
        IsCurrent = name == "main",
        TipCommitId = null,
    };

    /// <summary>Every member throws unless a test opts one in; enough of the interface is filled to compile.</summary>
    private sealed class FakeGateway : IGitGateway
    {
        public IReadOnlyList<BranchInfo> Branches { get; set; } = [];
        public Exception? CheckoutThrows { get; set; }
        public int GetBranchesCalls { get; private set; }

        public string RepositoryPath => @"C:\repos\demo";

        public Task<IReadOnlyList<BranchInfo>> GetBranchesAsync(CancellationToken ct = default)
        {
            GetBranchesCalls++;
            return Task.FromResult(Branches);
        }

        public Task CheckoutAsync(string refName, GitCheckoutOptions options, CancellationToken ct = default)
            => CheckoutThrows is { } ex ? Task.FromException(ex) : Task.CompletedTask;

        // ── unused members ──
        private static T No<T>() => throw new NotSupportedException("not exercised by this test");

        public Task<RepositoryInfo> GetRepositoryInfoAsync(CancellationToken ct = default) => No<Task<RepositoryInfo>>();
        public Task<Historia.Models.RepositoryStatus> GetStatusAsync(CancellationToken ct = default) => No<Task<Historia.Models.RepositoryStatus>>();
        public Task<IReadOnlyList<CommitSummary>> GetCommitsAsync(CommitQuery query, CancellationToken ct = default) => No<Task<IReadOnlyList<CommitSummary>>>();
        public Task<CommitDetail> GetCommitDetailAsync(ObjectId id, CancellationToken ct = default) => No<Task<CommitDetail>>();
        public Task<IReadOnlyList<FileStatusEntry>> GetCommitFilesAsync(ObjectId id, int parentIndex, CancellationToken ct = default) => No<Task<IReadOnlyList<FileStatusEntry>>>();
        public Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken ct = default) => No<Task<IReadOnlyList<TagInfo>>>();
        public Task<IReadOnlyList<RemoteInfo>> GetRemotesAsync(CancellationToken ct = default) => No<Task<IReadOnlyList<RemoteInfo>>>();
        public Task<FileDiff> GetFileDiffAsync(DiffRequest request, CancellationToken ct = default) => No<Task<FileDiff>>();
        public Task<AheadBehind?> GetAheadBehindAsync(string branchCanonicalName, CancellationToken ct = default) => No<Task<AheadBehind?>>();
        public Task<(string? Name, string? Email)> GetConfiguredIdentityAsync(CancellationToken ct = default) => No<Task<(string?, string?)>>();
        public Task<(string? Name, string? Email)> GetGlobalIdentityAsync(CancellationToken ct = default) => No<Task<(string?, string?)>>();
        public Task<bool> GetGpgSignConfiguredAsync(CancellationToken ct = default) => No<Task<bool>>();
        public Task StageAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => No<Task>();
        public Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => No<Task>();
        public Task DiscardAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => No<Task>();
        public Task<ObjectId> CommitAsync(CommitRequest request, CancellationToken ct = default) => No<Task<ObjectId>>();
        public Task MergeAsync(string refish, bool noFastForward, CancellationToken ct = default) => No<Task>();
        public Task ResetAsync(string target, GitResetMode mode, CancellationToken ct = default) => No<Task>();
        public int? CountCommitsLeavingResult { get; set; }
        public Task<int> CountCommitsLeavingAsync(string target, CancellationToken ct = default)
            => CountCommitsLeavingResult is { } n ? Task.FromResult(n) : No<Task<int>>();
        public Task<BranchInfo> CreateBranchAsync(string name, string startPoint, bool checkout, CancellationToken ct = default) => No<Task<BranchInfo>>();
        public Task DeleteBranchAsync(string name, bool force, CancellationToken ct = default) => No<Task>();
        public Task RenameBranchAsync(string oldName, string newName, CancellationToken ct = default) => No<Task>();
        public Task SetUpstreamAsync(string branchName, string upstreamRef, CancellationToken ct = default) => No<Task>();
        public Task<TagInfo> CreateTagAsync(string name, string target, string? message, CancellationToken ct = default) => No<Task<TagInfo>>();
        public Task DeleteTagAsync(string name, CancellationToken ct = default) => No<Task>();
        public Task AddRemoteAsync(string name, string url, CancellationToken ct = default) => No<Task>();
        public Task RemoveRemoteAsync(string name, CancellationToken ct = default) => No<Task>();
        public Task SetRemoteUrlAsync(string name, string url, CancellationToken ct = default) => No<Task>();
        public Task FetchAsync(string remote, GitFetchOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => No<Task>();
        public Task PullAsync(string remote, string branch, GitPullOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => No<Task>();
        public Task PushAsync(string remote, PushSpec spec, IProgress<GitProgress>? progress, CancellationToken ct = default) => No<Task>();
    }
}
