namespace Historia.Services.UI;

/// <summary>
/// App-side bridge for the Core <see cref="IAutoFetchService"/>. Reports a tab as fully
/// loaded via <see cref="RepositoryContext.FirstFullLoad"/> — forcing a still-deferred
/// session-restore tab through its initialize path first — and runs a background <c>git fetch</c>
/// through the same gateway a manual Fetch uses, flagged so routing never raises the
/// switch-to-Local-config prompt.
/// </summary>
public sealed class AutoFetchHost(IWorkspaceService workspace, ISettingsService settings) : IAutoFetchHost
{
    public Task WhenFullyLoadedAsync(RepositoryContext context, CancellationToken ct)
    {
        workspace.EnsureInitialized(context);
        return context.FirstFullLoad.WaitAsync(ct);
    }

    public async Task FetchAsync(RepositoryContext context, CancellationToken ct)
    {
        var remotes = context.State.Remotes;
        if (remotes.Count == 0)
            return;

        var remote = remotes.Any(r => r.Name == "origin") ? "origin" : remotes[0].Name;
        await context.Gateway
            .FetchAsync(remote, GitFetchOptions.From(settings.Current, background: true), progress: null, ct)
            .ConfigureAwait(false);

        // Land the new remote-tracking refs / ahead-behind in the UI, same as a manual fetch.
        await context.State.RefreshAllAsync(ct).ConfigureAwait(false);
    }
}
