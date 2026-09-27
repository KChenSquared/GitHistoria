namespace Historia.Services.Git;

public sealed partial class GitGateway
{
    public async Task CheckoutAsync(string refName, GitCheckoutOptions options, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        await context.WithRepositoryAsync(r => lib.Checkout(r, refName, options), ct).ConfigureAwait(false);
    }

    public async Task<BranchInfo> CreateBranchAsync(string name, string startPoint, bool checkout, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        return await context.WithRepositoryAsync(r => lib.CreateBranch(r, name, startPoint, checkout), ct).ConfigureAwait(false);
    }

    public async Task DeleteBranchAsync(string name, bool force, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        await context.WithRepositoryAsync(r => lib.DeleteBranch(r, name, force), ct).ConfigureAwait(false);
    }

    public async Task RenameBranchAsync(string oldName, string newName, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        await context.WithRepositoryAsync(r => lib.RenameBranch(r, oldName, newName), ct).ConfigureAwait(false);
    }

    public async Task<TagInfo> CreateTagAsync(string name, string target, string? message, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        return await context.WithRepositoryAsync(r => lib.CreateTag(r, name, target, message,
            message is null ? null : BuildTagger(r)), ct).ConfigureAwait(false);
    }

    public async Task DeleteTagAsync(string name, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        await context.WithRepositoryAsync(r => lib.DeleteTag(r, name), ct).ConfigureAwait(false);
    }

    public async Task AddRemoteAsync(string name, string url, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        await context.WithRepositoryAsync(r => lib.AddRemote(r, name, url), ct).ConfigureAwait(false);
    }

    public async Task RemoveRemoteAsync(string name, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        await context.WithRepositoryAsync(r => lib.RemoveRemote(r, name), ct).ConfigureAwait(false);
    }

    public async Task SetRemoteUrlAsync(string name, string url, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        await context.WithRepositoryAsync(r => lib.SetRemoteUrl(r, name, url), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> RenameRemoteAsync(string oldName, string newName, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        return await context.WithRepositoryAsync(r => lib.RenameRemote(r, oldName, newName), ct).ConfigureAwait(false);
    }

    private static Signature BuildTagger(Repository repo)
    {
        var name = repo.Config.Get<string>("user.name")?.Value ?? "Historia";
        var email = repo.Config.Get<string>("user.email")?.Value ?? "Historia@localhost";
        return new Signature(name, email, DateTimeOffset.Now);
    }
}
