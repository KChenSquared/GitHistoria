using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Historia.Services.Git;
using Historia.Services.Network;
using Historia.Services.Profiles;
using Historia.Services.Storage;
using Historia.Services.Workspace;
using Xunit;

namespace Historia.Tests;

public class WorkspaceServiceTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private RecentRepositoryService? _recents;

    private WorkspaceService NewWorkspace()
    {
        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var recents = _recents = new RecentRepositoryService(_host.Settings);
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        var identity = new RepoIdentityService(cli);
        return new WorkspaceService(_host.Provider, cli, recents, profiles, identity);
    }

    private static string Norm(string p) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)).ToLowerInvariant();

    private static TestRepositoryBuilder Repo(string file)
    {
        var b = TestRepositoryBuilder.Create();
        b.WriteFile(file, "x\n").Stage();
        b.Commit("c");
        return b;
    }

    /// <summary>
    /// Regression: re-opening an already-open repo used to call FocusInternal (which raises
    /// FocusedRepositoryChanged) from inside `lock (_sync)`. A handler marshalling to the UI
    /// thread then deadlocked against any other thread reading OpenRepositories / Focused.
    /// The event must be raised with the lock released.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Reopening_an_open_repo_does_not_raise_focus_event_under_the_lock()
    {
        var ws = NewWorkspace();
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");

        await ws.OpenAsync(r1.Path);
        await ws.OpenAsync(r2.Path);          // focus is now r2

        var otherThreadCouldRead = false;
        ws.FocusedRepositoryChanged += (_, _) =>
        {
            // A different thread reads state guarded by _sync. If the event fires while the
            // caller still holds _sync, this Wait times out — the deadlock, reproduced.
            var read = Task.Run(() => { _ = ws.OpenRepositories; _ = ws.Focused; });
            otherThreadCouldRead = read.Wait(TimeSpan.FromSeconds(5));
        };

        await ws.OpenAsync(r1.Path);          // already open → dup-guard → FocusInternal

        Assert.True(otherThreadCouldRead, "FocusedRepositoryChanged was raised while _sync was held");
        await ws.DisposeAsync();
    }

    // ── §V3-B/19.21: tab drag-and-drop reorder ──

    [Fact(Timeout = 30000)]
    public async Task Move_reorders_open_tabs_and_raises_open_changed()
    {
        var ws = NewWorkspace();
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");
        using var r3 = Repo("c.txt");

        var a = await ws.OpenAsync(r1.Path);
        await ws.OpenAsync(r2.Path);
        await ws.OpenAsync(r3.Path);

        var raised = 0;
        ws.OpenRepositoriesChanged += (_, _) => raised++;
        var focusedBefore = ws.Focused;

        ws.Move(a, 2); // a -> end

        Assert.Equal(1, raised);
        Assert.Equal(
            new[] { Norm(r2.Path), Norm(r3.Path), Norm(r1.Path) },
            ws.OpenRepositories.Select(c => Norm(c.RepositoryPath)));
        Assert.Same(focusedBefore, ws.Focused); // reorder does not change focus
        await ws.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task Move_clamps_the_index_and_ignores_a_closed_context()
    {
        var ws = NewWorkspace();
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");

        var a = await ws.OpenAsync(r1.Path);
        var b = await ws.OpenAsync(r2.Path);

        ws.Move(a, 99); // clamps to the last slot
        Assert.Equal(
            new[] { Norm(r2.Path), Norm(r1.Path) },
            ws.OpenRepositories.Select(c => Norm(c.RepositoryPath)));

        await ws.CloseAsync(b);
        var raised = false;
        ws.OpenRepositoriesChanged += (_, _) => raised = true;
        ws.Move(b, 0); // b is closed → no-op, no event
        Assert.False(raised);
        await ws.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task Move_persists_the_new_order_for_session_restore()
    {
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");

        var ws = NewWorkspace();
        var a = await ws.OpenAsync(r1.Path);
        await ws.OpenAsync(r2.Path);
        ws.Move(a, 1); // order is now r2, r1
        await Task.Delay(500); // let the debounced writer flush
        await ws.DisposeAsync();

        var node = JsonNode.Parse(File.ReadAllText(AppPaths.SessionFile))!;
        Assert.Equal(
            new[] { Norm(r2.Path), Norm(r1.Path) },
            node["paths"]!.AsArray().Select(n => Norm(n!.GetValue<string>())));
    }

    // ── §V3-9.3: session restore ──

    [Fact(Timeout = 30000)]
    public async Task Session_v1_array_still_loads()
    {
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");
        File.WriteAllText(AppPaths.SessionFile, JsonSerializer.Serialize(new[] { r1.Path, r2.Path }));

        var ws = NewWorkspace();
        await ws.RestoreSessionAsync();

        Assert.Equal(2, ws.OpenRepositories.Count);
        await ws.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task Session_v2_object_loads_and_restores_focus()
    {
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");
        using var r3 = Repo("c.txt");
        var payload = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["paths"] = new JsonArray(r1.Path, r2.Path, r3.Path),
            ["focusedPath"] = r2.Path,
        };
        File.WriteAllText(AppPaths.SessionFile, payload.ToJsonString());

        var ws = NewWorkspace();
        await ws.RestoreSessionAsync();

        Assert.Equal(3, ws.OpenRepositories.Count);
        Assert.Equal(Norm(r2.Path), Norm(ws.Focused!.RepositoryPath)); // focus, not last-opened
        // Prioritizing the focused tab's open must not reorder the tab strip -- it always has
        // to match the saved session order regardless of which tab was focused.
        Assert.Equal(
            new[] { Norm(r1.Path), Norm(r2.Path), Norm(r3.Path) },
            ws.OpenRepositories.Select(c => Norm(c.RepositoryPath)));
        await ws.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task Session_restore_does_not_touch_the_recent_list()
    {
        using var r1 = Repo("a.txt");
        using var r2 = Repo("b.txt");

        // Seed the Recent list with r1 only, then hand-write a session that restores both.
        var seed = NewWorkspace();
        await seed.OpenAsync(r1.Path);
        await Task.Delay(500);
        await seed.DisposeAsync();
        var recentsBefore = _recents!.Items.Select(i => Norm(i.Path)).ToArray();

        File.WriteAllText(AppPaths.SessionFile, new JsonObject
        {
            ["schemaVersion"] = 2,
            ["paths"] = new JsonArray(r1.Path, r2.Path),
            ["focusedPath"] = r2.Path,
        }.ToJsonString());

        var ws = NewWorkspace();
        await ws.RestoreSessionAsync();
        await Task.Delay(500);
        await ws.DisposeAsync();

        // r2 was restored but never explicitly opened, so it must not have joined Recent,
        // and r1's position/timestamp is unchanged.
        Assert.Equal(recentsBefore, _recents!.Items.Select(i => Norm(i.Path)).ToArray());
    }

    [Fact(Timeout = 30000)]
    public async Task Restoring_persists_a_v2_session_file_on_the_next_change()
    {
        using var r1 = Repo("a.txt");

        var ws = NewWorkspace();
        await ws.OpenAsync(r1.Path);
        await Task.Delay(500); // let the debounced writer flush
        await ws.DisposeAsync();

        var node = JsonNode.Parse(File.ReadAllText(AppPaths.SessionFile))!;
        Assert.Equal(2, node["schemaVersion"]!.GetValue<int>());
        Assert.Equal(Norm(r1.Path), Norm(node["focusedPath"]!.GetValue<string>()));
        Assert.Single(node["paths"]!.AsArray());
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
