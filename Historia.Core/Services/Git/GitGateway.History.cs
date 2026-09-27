namespace Historia.Services.Git;

public sealed partial class GitGateway
{

    public Task<CommitDetail> GetCommitDetailAsync(GitObjectId id, CancellationToken ct = default)
        => context.WithRepositoryAsync(r => lib.ProjectCommitDetail(r, id), ct);

    public Task<IReadOnlyList<FileStatusEntry>> GetCommitFilesAsync(GitObjectId id, int parentIndex, CancellationToken ct = default)
        => context.WithRepositoryAsync(r => lib.ProjectCommitFiles(r, id, parentIndex), ct);
}
