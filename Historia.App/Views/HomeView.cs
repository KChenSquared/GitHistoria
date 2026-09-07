using Historia.Services.Git;
using Historia.Services.Profiles;
using Historia.Services.Settings;
using static Historia.Views.ViewKit;

namespace Historia.Views;

/// <summary>Landing page — open / clone / init and the recents list (V4 port of Home.razor).</summary>
public sealed class HomeView : UserControl, IDisposable
{
    private readonly IWorkspaceService _workspace;
    private readonly IRecentRepositoryService _recents;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly INavigationBus _nav;
    private readonly IExternalLauncher _launcher;
    private readonly IProfileService _profiles;
    private readonly IRepoIdentityService _identity;
    private readonly IRepoLayoutService _repoLayout;
    private readonly Historia.Services.AutoFetch.IAutoFetchService _autoFetch;

    private readonly StackPanel _recentList = new() { Spacing = 4 };
    private readonly TextBlock _cloneStatus = Text("", "ColorSuccess", 12);
    private readonly Button _cloneCancel = TextButton("Cancel", () => { });
    private readonly Border _cloneBox = new();
    private bool _busy;

    public HomeView(IServiceProvider sp) : this(sp, "") { }

    /// <param name="intent">v8 M8: <c>"open"</c> / <c>"clone"</c> / <c>"init"</c> from the
    /// app-band <c>+</c> menu — the matching flow is launched once the panel is shown.</param>
    public HomeView(IServiceProvider sp, string intent)
    {
        _workspace = sp.GetRequiredService<IWorkspaceService>();
        _recents = sp.GetRequiredService<IRecentRepositoryService>();
        _dialogs = sp.GetRequiredService<IDialogService>();
        _notifications = sp.GetRequiredService<INotificationService>();
        _nav = sp.GetRequiredService<INavigationBus>();
        _launcher = sp.GetRequiredService<IExternalLauncher>();
        _profiles = sp.GetRequiredService<IProfileService>();
        _identity = sp.GetRequiredService<IRepoIdentityService>();
        _repoLayout = sp.GetRequiredService<IRepoLayoutService>();
        _autoFetch = sp.GetRequiredService<Historia.Services.AutoFetch.IAutoFetchService>();

        var root = new StackPanel { Spacing = 16, Margin = new Thickness(32), MaxWidth = 640 };

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(BindThemedMark(
            new Image { Width = 60, Height = 60, VerticalAlignment = VerticalAlignment.Center },
            sp.GetRequiredService<Historia.Services.Theming.IThemeService>()));
        title.Children.Add(Text("Historia", "ColorText", 28, FontWeight.SemiBold));
        root.Children.Add(title);
        root.Children.Add(Text("Navigate every timeline without the paradox.", "ColorTextMuted", 13));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(TextButton("Open repository…", () => _ = OpenAsync(), primary: true));
        actions.Children.Add(TextButton("Clone…", () => _ = CloneAsync()));
        actions.Children.Add(TextButton("New repository…", () => _ = InitAsync()));
        root.Children.Add(actions);

        _cloneBox.IsVisible = false;
        _cloneBox.Padding = new Thickness(10, 6);
        _cloneBox.CornerRadius = new CornerRadius(4);
        // Clone progress reads green like the "Stage all" pill (border + status text); Cancel
        // reads red like "Unstage all" (Button.danger chrome).
        _cloneBox.Dyn(Border.BackgroundProperty, "ColorSurface").Dyn(Border.BorderBrushProperty, "ColorSuccess");
        _cloneBox.BorderThickness = new Thickness(1);
        _cloneCancel.Classes.Add("danger");
        var cloneRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        cloneRow.Children.Add(_cloneStatus);
        cloneRow.Children.Add(_cloneCancel);
        _cloneBox.Child = cloneRow;
        root.Children.Add(_cloneBox);

        root.Children.Add(Text("Recent", "ColorTextMuted", 12, FontWeight.SemiBold));
        // Right padding so the auto-hiding scrollbar never sits on top of the remove ✕.
        root.Children.Add(new ScrollViewer
        {
            Content = _recentList,
            MaxHeight = 380,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 14, 0),
        });

        Content = ClosablePane(new ScrollViewer { Content = root }, sp, requireFocusedRepo: true);
        this.Dyn(BackgroundProperty, "ColorChrome");

        _recents.Changed += OnRecentsChanged;
        RenderRecents();

        var launch = intent switch
        {
            "open" => (Func<Task>)OpenAsync,
            "clone" => CloneAsync,
            "init" => InitAsync,
            _ => null,
        };
        if (launch is not null)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = launch());
    }

    private void OnRecentsChanged(object? s, EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(RenderRecents);

    private void RenderRecents()
    {
        _recentList.Children.Clear();
        if (_recents.Items.Count == 0)
        {
            _recentList.Children.Add(Text("No repositories opened yet.", "ColorTextMuted"));
            return;
        }
        foreach (var r in _recents.Items)
        {
            var rr = r;
            var label = new StackPanel
            {
                Margin = new Thickness(10, 6),
                Children =
                {
                    Text(r.DisplayName + (r.IsStale ? "  (missing)" : ""), "ColorText", 13),
                    Text(r.Path, "ColorTextFaint", 11),
                },
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(label);
            var remove = IconButton("close", "Remove", () => _ = _recents.RemoveAsync(rr.Path));
            Grid.SetColumn(remove, 1);
            grid.Children.Add(remove);

            // Transparent fill + grey outline at rest; list-hover fill on pointer-over
            // (matches Button.secondary / V3 .recent-item).
            var row = new Border
            {
                Child = grid,
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Cursor = new Avalonia.Input.Cursor(
                    r.IsStale ? Avalonia.Input.StandardCursorType.Arrow : Avalonia.Input.StandardCursorType.Hand),
            }.Dyn(Border.BorderBrushProperty, "ColorBorderStrong");
            row.WithRevealMenu(_launcher, () => rr.Path, "Open repository location");
            row.Opacity = r.IsStale ? 0.5 : 1;
            row.PointerEntered += (_, _) => { if (!rr.IsStale) row.Dyn(Border.BackgroundProperty, "ColorListHover"); };
            row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
            row.PointerPressed += (_, e) =>
            {
                if (!rr.IsStale && !_busy && e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
                    _ = OpenPathAsync(rr.Path);
            };
            _recentList.Children.Add(row);
        }
    }

    private async Task OpenAsync()
    {
        var path = await _dialogs.PickFolderAsync("Open a Git repository");
        if (path is not null)
            await OpenPathAsync(path);
    }

    private async Task OpenPathAsync(string path)
    {
        _busy = true;
        try
        {
            // §12.1.1: on a cold start this tells the auto-fetch loop to run an [InitialFetch].
            _autoFetch.NotifyRepoEntering(Historia.Services.AutoFetch.RepoEntrySource.Opened);
            await _workspace.OpenAsync(path);
            _nav.RequestNavigate("repo");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, $"Could not open repository: {ex.Message}", ex.NotificationDetails());
        }
        finally { _busy = false; }
    }

    private async Task InitAsync()
    {
        var path = await _dialogs.PickFolderAsync("Choose a folder for the new repository");
        if (path is null) return;
        var branch = await _dialogs.PromptAsync("Initial branch", "Name for the initial branch:", "main");
        if (branch is null) return;

        _busy = true;
        try
        {
            // §12.1.1: a fresh init is already current — start the loop with no [InitialFetch].
            _autoFetch.NotifyRepoEntering(Historia.Services.AutoFetch.RepoEntrySource.ClonedOrInitialized);
            await _workspace.InitAsync(path, string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim());
            _nav.RequestNavigate("repo");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, $"Could not initialize repository: {ex.Message}", ex.NotificationDetails());
        }
        finally { _busy = false; }
    }

    /// <summary>Parent of the most recently opened repo, else the user's home folder.</summary>
    private string DefaultCloneRoot()
    {
        var recent = _recents.Items.FirstOrDefault()?.Path;
        if (!string.IsNullOrEmpty(recent))
        {
            var parent = System.IO.Path.GetDirectoryName(recent.TrimEnd('/', '\\'));
            if (!string.IsNullOrEmpty(parent))
                return parent;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private async Task CloneAsync()
    {
        var r = await Historia.Platform.CloneDialog.ShowAsync(_dialogs, DefaultCloneRoot(), GuessRepoName, _profiles.GetAllProfiles());
        if (r is null) return;

        var options = new CloneOptions
        {
            Depth = r.Shallow ? 1 : null,
            Sparse = r.Sparse,
        };

        _busy = true;
        var cts = new CancellationTokenSource();
        var progress = new Progress<GitProgress>(p =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _cloneStatus.Text = $"{p.Phase} {(p.Percent is { } pct ? $"{pct:0}%" : "")}".Trim();
                _cloneBox.IsVisible = true;
            }));
        void CancelClone(object? s, Avalonia.Interactivity.RoutedEventArgs e) => cts.Cancel();
        _cloneCancel.Click += CancelClone;
        _cloneStatus.Text = "Starting clone…";
        _cloneBox.IsVisible = true;
        try
        {
            // §12.1.1: a fresh clone is already current — start the loop with no [InitialFetch].
            _autoFetch.NotifyRepoEntering(Historia.Services.AutoFetch.RepoEntrySource.ClonedOrInitialized);
            var ctx = await _workspace.CloneAsync(r.Url, r.TargetDirectory, options, progress, cts.Token);
            await ApplyCheckoutIdentityAsync(ctx, r.ProfileId);
            _nav.RequestNavigate("repo");
        }
        catch (OperationCanceledException)
        {
            _notifications.Publish(NotificationSeverity.Info, "Clone cancelled.");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, $"Clone failed: {ex.Message}", ex.NotificationDetails());
        }
        finally { _busy = false; cts.Dispose(); _cloneBox.IsVisible = false; _cloneCancel.Click -= CancelClone; }
    }

    /// <summary>§13.4: binds the clone dialog's chosen checkout identity to the freshly cloned
    /// repo — a profile, or the <i>Local config</i> marker when none was chosen.</summary>
    private async Task ApplyCheckoutIdentityAsync(RepositoryContext ctx, Guid? profileId)
    {
        try
        {
            var key = ctx.IdentityKey ?? await _identity.ResolveAsync(ctx, ctx.Lifetime);

            if (profileId is not { } id)
            {
                await _profiles.SetLocalConfigForRepositoryAsync(key, ctx.RepositoryPath, ctx.Lifetime);
                ctx.State.SetActiveProfile(null);
                return;
            }
            var profile = _profiles.GetAllProfiles().FirstOrDefault(p => p.Id == id);
            if (profile is null)
                return;

            await _profiles.BindProfileToRepositoryAsync(key, ctx.RepositoryPath, profile.Id, ctx.Lifetime);
            ctx.State.SetActiveProfile(profile);
        }
        catch (Exception ex)
        {
            // Best-effort: the clone itself already succeeded, so a binding hiccup is a
            // notice, not a failure of the clone.
            _notifications.Publish(NotificationSeverity.Warning,
                $"Cloned, but couldn't apply the chosen profile/network mode: {ex.Message}", ex.NotificationDetails());
        }
    }

    private static string GuessRepoName(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        var lastSlash = trimmed.LastIndexOfAny(['/', ':']);
        var name = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    public void Dispose() => _recents.Changed -= OnRecentsChanged;
}
