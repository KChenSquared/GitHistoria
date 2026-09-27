namespace Historia.Services.Git;

public sealed partial class GitGateway
{
    public Task<FileDiff> GetFileDiffAsync(DiffRequest request, CancellationToken ct = default)
        => context.WithRepositoryAsync(r => lib.ProjectDiff(r, request), ct);
}
