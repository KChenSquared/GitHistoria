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
    public void V1_file_migrates_global_visibility_to_defaults_and_keeps_sshOnly()
    {
        // A schema-v1 file: per-repo column lists + sshOnly, no `global` block.
        var v1 = """
        {
          "schemaVersion": 1,
          "repos": {
            "c:\\work\\myrepo": {
              "columns": [
                { "kind": "Refs", "width": 140, "visible": false },
                { "kind": "Graph", "width": 999, "visible": true },
                { "kind": "Message", "width": 400, "visible": true, "isFlex": true }
              ],
              "graphWidthPinned": true,
              "sshOnly": true
            }
          }
        }
        """;
        File.WriteAllText(AppPaths.RepoLayoutFile, v1);

        var svc = new RepoLayoutService();

        // Global visibility is the defaults (Refs, Graph, Message visible), not the v1 list.
        Assert.Equal(
            GraphColumnDefaults.All.Select(c => (c.Kind, c.Visible, c.IsFlex)),
            svc.GlobalColumns.Select(c => (c.Kind, c.Visible, c.IsFlex)));
        // sshOnly is carried over; the pin and widths are not.
        Assert.True(svc.Get(Repo).SshOnly);
        Assert.False(svc.Get(Repo).GraphWidthPinned);
        Assert.Equal(220, svc.Get(Repo).Columns.Single(c => c.Kind == GraphColumnKind.Graph).Width, 3);
    }

    [Fact]
    public async Task Widths_are_not_persisted_across_a_reload()
    {
        var svc = new RepoLayoutService();
        await svc.UpdateAsync(Repo, l => l with
        {
            Columns = l.Columns.Select(c => c.Kind == GraphColumnKind.Graph ? c with { Width = 900 } : c).ToList(),
        });
        await svc.FlushAsync();

        var reloaded = new RepoLayoutService();
        Assert.Equal(220, reloaded.Get(Repo).Columns.Single(c => c.Kind == GraphColumnKind.Graph).Width, 3);
    }

    [Fact]
    public async Task Global_visibility_change_raises_Changed_with_the_global_key_and_persists()
    {
        var svc = new RepoLayoutService();
        string? seenKey = null;
        svc.Changed += (_, e) => { if (e.RepoPath == IRepoLayoutService.GlobalKey) seenKey = e.RepoPath; };

        await svc.UpdateGlobalAsync(cols =>
            cols.Select(c => c.Kind == GraphColumnKind.Author ? c with { Visible = true } : c).ToList());

        Assert.Equal(IRepoLayoutService.GlobalKey, seenKey);
        Assert.True(svc.GlobalColumns.Single(c => c.Kind == GraphColumnKind.Author).Visible);

        await svc.FlushAsync();
        var reloaded = new RepoLayoutService();
        Assert.True(reloaded.GlobalColumns.Single(c => c.Kind == GraphColumnKind.Author).Visible);
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
