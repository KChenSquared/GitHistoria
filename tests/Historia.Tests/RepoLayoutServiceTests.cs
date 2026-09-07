namespace Historia.Tests;

/// <summary>§V3-B: per-repository graph layout store.</summary>
public class RepoLayoutServiceTests : IDisposable
{
    private readonly StateDir _state = new();
    private const string Repo = @"C:\work\MyRepo";

    public void Dispose() => _state.Dispose();

    [Fact]
    public void Unknown_repo_returns_defaults()
    {
        var svc = new RepoLayoutService();
        var l = svc.Get(Repo);

        Assert.Equal(GraphColumnDefaults.All, l.Columns);
        Assert.False(l.GraphWidthPinned);
        Assert.False(l.SshOnly);
    }

    [Fact]
    public async Task Update_round_trips_per_path_and_persists()
    {
        var svc = new RepoLayoutService();
        await svc.UpdateAsync(Repo, l => l with
        {
            Columns = l.Columns.Select(c => c.Kind == GraphColumnKind.Graph ? c with { Width = 175 } : c).ToList(),
            GraphWidthPinned = true,
            SshOnly = true,
        });

        Assert.Equal(175, svc.Get(Repo).Columns.Single(c => c.Kind == GraphColumnKind.Graph).Width, 3);
        Assert.True(svc.Get(Repo).GraphWidthPinned);
        Assert.True(svc.Get(Repo).SshOnly);

        // A different repo is unaffected.
        Assert.False(svc.Get(@"D:\other").SshOnly);

        // Re-read from disk: widths + pin reset, SshOnly survives.
        await svc.FlushAsync();
        var reloaded = new RepoLayoutService();
        Assert.Equal(220, reloaded.Get(Repo).Columns.Single(c => c.Kind == GraphColumnKind.Graph).Width, 3);
        Assert.False(reloaded.Get(Repo).GraphWidthPinned);
        Assert.True(reloaded.Get(Repo).SshOnly);
    }

    [Fact]
    public async Task Key_is_path_normalised()
    {
        var svc = new RepoLayoutService();
        await svc.UpdateAsync(Repo, l => l with { SshOnly = true });

        Assert.True(svc.Get(Repo + @"\").SshOnly);
        Assert.True(svc.Get(Repo.ToUpperInvariant()).SshOnly);
    }
}
