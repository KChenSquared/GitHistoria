namespace HistoriaApp.Tests;

/// <summary>The remote context menu's item order, click routing and pure rules (push-URL
/// decision, edit validation, delete confirmation text).</summary>
[Trait("tier", "ui")]
public sealed class RemoteMenuTests
{
    private sealed class RecordingLauncher : IExternalLauncher
    {
        public List<string> Opened { get; } = [];
        public void OpenUrl(string url) => Opened.Add(url);
        public void RevealInFileManager(string path) { }
        public void OpenFolder(string path) { }
    }

    private static RemoteInfo Remote(string url, string? pushUrl = null, string name = "origin") =>
        new() { Name = name, Url = url, PushUrl = pushUrl ?? url };

    // Build only captures its collaborators for the click handlers, so the tests that never click
    // a dialog-backed item pass null for them.
    private static List<Control> BuildMenu(
        RemoteInfo remote, IRepoActionBus? actions = null, IExternalLauncher? launcher = null, Func<string, Task?>? copy = null) =>
        RemoteMenu.Build(remote, null!, null!, null!, actions ?? new RepoActionBus(),
            launcher ?? new RecordingLauncher(), copy ?? (_ => null));

    private static string Header(Control c) => Assert.IsType<MenuItem>(c).Header!.ToString()!;

    private static Control ItemWithHeader(IEnumerable<Control> items, string header) =>
        items.First(i => i is MenuItem m && m.Header?.ToString() == header);

    private static void Click(Control item) =>
        ((MenuItem)item).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

    [AvaloniaFact]
    public void Menu_order_matches_spec_for_https_remote()
    {
        var items = BuildMenu(Remote("https://example.com/o/r.git"));

        Assert.Equal(10, items.Count);
        var expected = new (int Index, string? Header)[]
        {
            (0, "Open In Browser"), (1, null), (2, "Fetch"), (3, "Prune"), (4, null),
            (5, "Edit…"), (6, "Set Push URL…"), (7, "Delete…"), (8, null), (9, "Copy URL"),
        };
        foreach (var (index, header) in expected)
        {
            if (header is null) Assert.IsType<Separator>(items[index]);
            else Assert.Equal(header, Header(items[index]));
        }
    }

    [AvaloniaFact]
    public void Menu_omits_open_in_browser_for_local_path_remote()
    {
        var items = BuildMenu(Remote(@"C:\repos\bare.git"));

        Assert.Equal(8, items.Count);
        Assert.Equal("Fetch", Header(items[0]));
        Assert.DoesNotContain(items.OfType<MenuItem>(), m => m.Header?.ToString() == "Open In Browser");
        Assert.IsType<Separator>(items[2]);
        Assert.IsType<Separator>(items[6]);
        Assert.Equal("Copy URL", Header(items[7]));
    }

    [AvaloniaFact]
    public void Fetch_and_prune_request_the_targeted_network_action()
    {
        var bus = new RepoActionBus();
        var requests = new List<RepoNetworkRequest>();
        bus.NetworkActionRequested += (_, r) => requests.Add(r);
        var items = BuildMenu(Remote("https://example.com/o/r.git", name: "upstream"), bus);

        Click(ItemWithHeader(items, "Fetch"));
        Click(ItemWithHeader(items, "Prune"));

        Assert.Equal(
            [new RepoNetworkRequest(RepoNetworkAction.Fetch, "upstream", null),
             new RepoNetworkRequest(RepoNetworkAction.Prune, "upstream", null)],
            requests);
    }

    [AvaloniaFact]
    public void Open_in_browser_and_copy_url_use_the_expected_urls()
    {
        var launcher = new RecordingLauncher();
        var copied = new List<string>();
        var items = BuildMenu(Remote("git@example.com:o/r.git"), launcher: launcher,
            copy: s => { copied.Add(s); return null; });

        Click(ItemWithHeader(items, "Open In Browser"));
        Click(ItemWithHeader(items, "Copy URL"));

        Assert.Equal(["https://example.com/o/r"], launcher.Opened);
        Assert.Equal(["git@example.com:o/r.git"], copied);
    }

    [Fact]
    public void DecidePushUrl_empty_with_distinct_push_url_clears() =>
        Assert.Equal(RemoteMenu.PushUrlChange.Clear,
            RemoteMenu.DecidePushUrl(Remote("https://a/r.git", "https://b/r.git"), "  "));

    [Fact]
    public void DecidePushUrl_empty_without_push_url_is_noop() =>
        Assert.Equal(RemoteMenu.PushUrlChange.NoOp, RemoteMenu.DecidePushUrl(Remote("https://a/r.git"), ""));

    [Fact]
    public void DecidePushUrl_fetch_url_clears_or_is_noop()
    {
        Assert.Equal(RemoteMenu.PushUrlChange.Clear,
            RemoteMenu.DecidePushUrl(Remote("https://a/r.git", "https://b/r.git"), " https://a/r.git "));
        Assert.Equal(RemoteMenu.PushUrlChange.NoOp,
            RemoteMenu.DecidePushUrl(Remote("https://a/r.git"), "https://a/r.git"));
    }

    [Fact]
    public void DecidePushUrl_current_push_url_is_noop() =>
        Assert.Equal(RemoteMenu.PushUrlChange.NoOp,
            RemoteMenu.DecidePushUrl(Remote("https://a/r.git", "https://b/r.git"), "https://b/r.git"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecidePushUrl_new_value_sets(bool hasDistinct)
    {
        var remote = hasDistinct ? Remote("https://a/r.git", "https://b/r.git") : Remote("https://a/r.git");
        Assert.Equal(RemoteMenu.PushUrlChange.Set("https://c/r.git"), RemoteMenu.DecidePushUrl(remote, " https://c/r.git "));
    }

    [Theory]
    [InlineData("", "https://a/r.git", false)]
    [InlineData("   ", "https://a/r.git", false)]
    [InlineData("my remote", "https://a/r.git", false)]
    [InlineData("/lead", "https://a/r.git", false)]
    [InlineData("trail/", "https://a/r.git", false)]
    [InlineData("a..b", "https://a/r.git", false)]
    [InlineData("taken", "https://a/r.git", false)]
    [InlineData("origin", "", false)]
    [InlineData("origin", "https://a/r .git", false)]
    [InlineData("origin", "https://a/r.git", true)]
    [InlineData("team/fork", "https://a/r.git", true)]
    public void EditRemoteDialog_Validate(string name, string url, bool valid)
    {
        var problem = Historia.Platform.EditRemoteDialog.Validate(name, url, ["taken", "other"]);
        Assert.Equal(valid, problem is null);
    }

    [Fact]
    public void Delete_effect_names_remote_url_and_branch_count()
    {
        var effect = RemoteMenu.DeleteEffect("origin", "https://example.com/o/r.git", 65);

        Assert.Contains("'origin'", effect);
        Assert.Contains("https://example.com/o/r.git", effect);
        Assert.Contains("65 remote-tracking branches", effect);
        Assert.Contains("origin/", effect);
        Assert.Contains("lose their upstream", effect);
        Assert.Contains("become unreferenced locally", effect);
        Assert.DoesNotContain("Are you sure", effect);
    }

    [Fact]
    public void Delete_effect_handles_zero_and_one_branch()
    {
        Assert.Contains("no remote-tracking branches", RemoteMenu.DeleteEffect("origin", "u", 0));
        Assert.Contains("1 remote-tracking branch (", RemoteMenu.DeleteEffect("origin", "u", 1));
    }
}
