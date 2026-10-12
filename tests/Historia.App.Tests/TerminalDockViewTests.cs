namespace HistoriaApp.Tests;

/// <summary>The dock view over a real session service and a stub pty: the markup shows and sizes
/// the dock from the view model, draws one tab per session and routes tab clicks and the splitter
/// drag to it.</summary>
[Trait("tier", "ui")]
public sealed class TerminalDockViewTests
{
    private sealed class StubPty : IPtyConnection
    {
        public void Write(ReadOnlySpan<byte> data) { }
        public void Resize(int columns, int rows) { }
        public event Action<ReadOnlyMemory<byte>>? OutputReceived { add { } remove { } }
        public event Action<int>? Exited { add { } remove { } }
        public void Kill() { }
        public void Dispose() { }
    }

    private sealed class StubBackend : ITerminalBackend
    {
        public Task<IPtyConnection> StartAsync(PtyStartInfo info, CancellationToken ct) => Task.FromResult<IPtyConnection>(new StubPty());
    }

    private sealed class StubThemeService : IThemeService
    {
        public EffectiveTheme Effective { get; set; } = EffectiveTheme.Dark;
        public ThemeMode CurrentMode => ThemeMode.Dark;
        public bool HighContrastEnabled => false;
        public string EffectiveCssThemeName => "dark";
        public IReadOnlyDictionary<string, Rgb> Palette => HistoriaPalette.For(Effective);
        public IReadOnlyList<Rgb> LaneColors => [];
#pragma warning disable CS0067
        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
#pragma warning restore CS0067
        public Task ApplyModeAsync(ThemeMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public bool CustomThemeActive => false;
        public Task SetCustomThemeEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task SelectCustomThemeAsync(string themeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHighContrastAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubSettings : ISettingsService
    {
        public AppSettings Current { get; private set; } = new();
        public event EventHandler<SettingsChangedEventArgs>? Changed;

        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default)
        {
            var old = Current;
            Current = mutate(old);
            Changed?.Invoke(this, new SettingsChangedEventArgs(old, Current));
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubWorkspace : IWorkspaceService
    {
        public IReadOnlyList<RepositoryContext> OpenRepositories => [];
        public RepositoryContext? Focused => null;
        public Task<RepositoryContext> OpenAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RepositoryContext> InitAsync(string path, string? initialBranch, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RepositoryContext> CloneAsync(string url, string targetDirectory, CloneOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RepositoryContext> OpenSubmoduleAsync(RepositoryContext owner, SubmoduleInfo submodule, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CloseAsync(RepositoryContext context) => throw new NotSupportedException();
        public void Focus(RepositoryContext context) { }
        public void EnsureInitialized(RepositoryContext context) { }
        public void Move(RepositoryContext context, int newIndex) { }
        public Task RestoreSessionAsync(CancellationToken ct = default) => Task.CompletedTask;
#pragma warning disable CS0067
        public event EventHandler<FocusedRepositoryChangedEventArgs>? FocusedRepositoryChanged;
        public event EventHandler<OpenRepositoriesChangedEventArgs>? OpenRepositoriesChanged;
#pragma warning restore CS0067
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            var settings = new StubSettings();
            var ui = new Historia.Platform.UiDispatcher();
            Sessions = new TerminalSessionService(new StubBackend(), settings, ui, new TerminalHostEnvironment
            {
                Platform = OSPlatformKind.Linux,
                FileExists = p => p == "/bin/bash",
                DirectoryExists = _ => true,
                GetEnvironmentVariable = _ => null,
                FindOnPath = _ => null,
                GetPasswdShell = () => "/bin/bash",
                GetInheritedEnvironment = () => new Dictionary<string, string> { ["PATH"] = "/usr/bin" },
                HomeDirectory = "/home/me",
                CurrentDirectory = "/tmp",
                ExecutablePath = "/opt/historia/Historia",
                Version = "5.1.0",
            });

            var sc = new ServiceCollection();
            sc.AddSingleton<ITerminalSessionService>(Sessions);
            sc.AddSingleton<IWorkspaceService, StubWorkspace>();
            sc.AddSingleton<ISettingsService>(settings);
            sc.AddSingleton<IKeyboardCommandRouter, KeyboardCommandRouter>();
            sc.AddSingleton<IThemeService, StubThemeService>();
            sc.AddSingleton<IUiDispatcher>(ui);
            sc.AddSingleton<IExternalLauncher, ExternalLauncher>();

            Dock = new TerminalDockView();
            Dock.Initialize(sc.BuildServiceProvider());
            var host = new Grid { Height = 800 };
            host.Children.Add(Dock);
            Window = new Window { Width = 900, Height = 800, Content = host };
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public TerminalSessionService Sessions { get; }
        public TerminalDockView Dock { get; }
        public Window Window { get; }

        public static TerminalOrigin Repo(string name)
            => TerminalOrigin.ForRepository(RepoIdentityKey.FromExplicitId(Guid.NewGuid()), name, $"/src/{name}", name);

        public IReadOnlyList<Button> TabButtons()
            => [.. Dock.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("dockTab"))];

        public void Dispose()
        {
            Window.Close();
            Dock.Dispose();
        }
    }

    [AvaloniaFact]
    public void A_new_dock_is_hidden_with_no_height()
    {
        using var f = new Fixture();

        Assert.False(f.Dock.IsVisible);
        Assert.Equal(0, f.Dock.Height);
        Assert.Empty(f.TabButtons());
    }

    [AvaloniaFact]
    public async Task Activation_shows_the_dock_with_a_tab_for_each_session()
    {
        using var f = new Fixture();
        var first = await f.Sessions.OpenOrFocusAsync(Fixture.Repo("alpha"));
        await f.Sessions.OpenOrFocusAsync(Fixture.Repo("beta"));
        f.Sessions.RequestActivation();
        Dispatcher.UIThread.RunJobs();

        Assert.True(f.Dock.IsVisible);
        Assert.True(f.Dock.Height >= TerminalDockViewModel.MinDockHeight);
        var tabs = f.TabButtons();
        Assert.Equal(2, tabs.Count);
        Assert.DoesNotContain("active", tabs[0].Classes);
        Assert.Contains("active", tabs[1].Classes);
        Assert.NotSame(first, f.Sessions.Focused);
    }

    [AvaloniaFact]
    public async Task Clicking_a_tab_focuses_its_session_and_marks_it_active()
    {
        using var f = new Fixture();
        var first = await f.Sessions.OpenOrFocusAsync(Fixture.Repo("alpha"));
        await f.Sessions.OpenOrFocusAsync(Fixture.Repo("beta"));
        f.Sessions.RequestActivation();
        Dispatcher.UIThread.RunJobs();

        f.TabButtons()[0].Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(first, f.Sessions.Focused);
        var tabs = f.TabButtons();
        Assert.Contains("active", tabs[0].Classes);
        Assert.DoesNotContain("active", tabs[1].Classes);
    }

    [AvaloniaFact]
    public async Task Dragging_the_splitter_up_makes_the_dock_taller()
    {
        using var f = new Fixture();
        await f.Sessions.OpenOrFocusAsync(Fixture.Repo("alpha"));
        f.Sessions.RequestActivation();
        Dispatcher.UIThread.RunJobs();
        var before = f.Dock.Height;

        f.Dock.Splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragDeltaEvent, Vector = new Vector(0, -50) });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before + 50, f.Dock.Height);
    }

    [AvaloniaFact]
    public async Task Closing_the_last_session_hides_the_dock()
    {
        using var f = new Fixture();
        var session = await f.Sessions.OpenOrFocusAsync(Fixture.Repo("alpha"));
        f.Sessions.RequestActivation();
        Dispatcher.UIThread.RunJobs();
        Assert.True(f.Dock.IsVisible);

        await f.Sessions.CloseAsync(session.Id);
        Dispatcher.UIThread.RunJobs();

        Assert.False(f.Dock.IsVisible);
        Assert.Equal(0, f.Dock.Height);
    }
}
