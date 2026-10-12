namespace HistoriaApp.Tests;

/// <summary>The remote context menu's item order and click routing, and the Edit dialog's validation.</summary>
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
    [InlineData("-x", "https://a/r.git", false)]
    [InlineData("--upload-pack=evil", "https://a/r.git", false)]
    [InlineData("origin", "git@host:o/taken.git", false)]
    [InlineData("origin", "  git@host:o/taken.git  ", false)]
    public void EditRemoteDialog_Validate(string name, string url, bool valid)
    {
        var problem = Historia.Platform.EditRemoteDialog.Validate(name, url, ["taken", "other"],
            new Dictionary<string, string> { ["git@host:o/taken.git"] = "taken" });
        Assert.Equal(valid, problem is null);
    }

    [Fact]
    public void Validate_duplicate_url_names_the_remote_using_it()
    {
        var owners = Historia.Platform.EditRemoteDialog.UrlOwners(
            [Remote("git@host:o/up.git", name: "upstream"), Remote(" git@host:o/r.git ", name: "origin")]);

        Assert.Equal("Remote 'upstream' already uses this URL.",
            Historia.Platform.EditRemoteDialog.Validate("fork", "git@host:o/up.git", ["upstream", "origin"], owners));
        Assert.Equal("Remote 'origin' already uses this URL.",
            Historia.Platform.EditRemoteDialog.Validate("fork", "git@host:o/r.git ", ["upstream", "origin"], owners));
    }

    [Fact]
    public void Validate_accepts_a_remote_keeping_its_own_url_in_edit()
    {
        // Edit excludes the remote being edited from both lists, so its own URL is not a clash.
        var origin = Remote("git@host:o/r.git");
        var others = new[] { Remote("git@host:o/up.git", name: "upstream") };

        Assert.Null(Historia.Platform.EditRemoteDialog.Validate(origin.Name, origin.Url,
            others.Select(r => r.Name).ToList(), Historia.Platform.EditRemoteDialog.UrlOwners(others)));
    }

    [Fact]
    public void Validate_rejects_a_leading_dash_with_its_own_message() =>
        Assert.Equal("A remote name cannot start with '-'.",
            Historia.Platform.EditRemoteDialog.Validate("-x", "git@host:o/r.git", [], new Dictionary<string, string>()));
}
