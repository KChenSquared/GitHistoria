namespace Historia.Controls;

public enum ShortcutsBarSide
{
    Left,
    Right,
}

/// <summary>
/// A static shortcut bar positioned above a side pane (left above sidebar, right above detail).
/// Height matches <see cref="CommitGraphControl.HeaderHeight"/> (30 px), forming a continuous
/// chrome strip with the commit graph header band across the window.
/// </summary>
internal sealed class ShortcutsBarControl : UserControl, IDisposable
{
    public const double BarHeight = CommitGraphControl.HeaderHeight;

    private const double GroupGap = 12;

    private readonly IServiceProvider _sp;
    private readonly IWorkspaceService? _workspace;
    private readonly IRepoActionBus? _actions;
    private readonly ISettingsService? _settings;
    private readonly INavigationBus? _nav;
    private readonly INotificationService? _notifications;
    private readonly IStashService? _stash;
    private readonly IDialogService? _dialogs;
    private readonly ShortcutsBarSide _side;
    private readonly Control _container;
    private readonly StackPanel[] _groups;
    private readonly Border _border;

    private readonly Button? _branchBtn;
    private readonly Button? _terminalBtn;
    private readonly Button? _fetchBtn;
    private readonly Button? _pullBtn;
    private readonly Button? _pushBtn;
    private readonly Button? _stashBtn;

    private bool _disposed;
    private double _lastReportedNaturalWidth = -1;

    public ShortcutsBarSide Side => _side;

    public double NaturalWidth
    {
        get
        {
            double sum = 0;
            foreach (var group in _groups)
            {
                if (!group.IsMeasureValid && VisualRoot == null)
                    group.Measure(new Size(double.PositiveInfinity, BarHeight));

                sum += group.DesiredSize.Width;
            }

            if (sum <= 0 && _lastReportedNaturalWidth > 0)
                return _lastReportedNaturalWidth;

            if (_groups.Length > 1)
                sum += GroupGap;

            return sum + _border.Padding.Left + _border.Padding.Right;
        }
    }

    public event EventHandler? NaturalWidthChanged;

    public ShortcutsBarControl(IServiceProvider sp, ShortcutsBarSide side)
    {
        _sp = sp;
        _side = side;
        _workspace = sp?.GetService<IWorkspaceService>();
        _actions = sp?.GetService<IRepoActionBus>();
        _settings = sp?.GetService<ISettingsService>();
        _nav = sp?.GetService<INavigationBus>();
        _notifications = sp?.GetService<INotificationService>();
        _stash = sp?.GetService<IStashService>();
        _dialogs = sp?.GetService<IDialogService>();

        if (side == ShortcutsBarSide.Left)
        {
            _branchBtn = IconTextButton("graph_8", "Branch", "Create a branch at the selected commit",
                () => _ = CreateBranchAsync());
            _terminalBtn = IconTextButton("terminal", null, "Open terminal in this repository", () =>
            {
                if (_workspace?.Focused is { } ctx)
                    _ = _sp?.GetService<ITerminalSessionService>()
                           ?.OpenOrFocusAsync(AppBandControl.RepoOrigin(ctx));
            });

            var leadGroup = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            leadGroup.Children.Add(_branchBtn);

            var trailGroup = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            trailGroup.Children.Add(_terminalBtn);

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            };
            Grid.SetColumn(leadGroup, 0);
            Grid.SetColumn(trailGroup, 2);
            grid.Children.Add(leadGroup);
            grid.Children.Add(trailGroup);

            _container = grid;
            _groups = [leadGroup, trailGroup];
        }
        else
        {
            _fetchBtn = IconTextButton("sync", "Fetch", "Fetch from the remote",
                () => _actions?.RequestNetworkAction(RepoNetworkAction.Fetch));
            _pullBtn = IconTextButton("cloud_download", "Pull", "Pull from the remote",
                () => _actions?.RequestNetworkAction(RepoNetworkAction.Pull));
            _pushBtn = IconTextButton("cloud_upload", "Push", "Push to the remote",
                () => _actions?.RequestNetworkAction(RepoNetworkAction.Push));
            _stashBtn = IconTextButton("save", "Stash", "Stash all changes",
                () => _ = StashAllAsync());

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            panel.Children.Add(_fetchBtn);
            panel.Children.Add(_pullBtn);
            panel.Children.Add(_pushBtn);
            panel.Children.Add(_stashBtn);

            _container = panel;
            _groups = [panel];
        }

        _border = new Border
        {
            Height = BarHeight,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(6, 0),
            Child = _container,
        }.Dyn(Border.BackgroundProperty, "ColorChrome").Dyn(Border.BorderBrushProperty, "ColorBorder");

        Content = _border;
        Height = BarHeight;

        _container.LayoutUpdated += OnPanelLayoutUpdated;

        if (_workspace is not null)
            _workspace.FocusedRepositoryChanged += OnFocusChanged;

        _lastReportedNaturalWidth = NaturalWidth;
        Sync();
    }

    private void OnFocusChanged(object? s, FocusedRepositoryChangedEventArgs e)
        => Dispatcher.UIThread.Post(Sync);

    private void OnPanelLayoutUpdated(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        var current = NaturalWidth;
        if (Math.Abs(current - _lastReportedNaturalWidth) > 0.5)
        {
            _lastReportedNaturalWidth = current;
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                    NaturalWidthChanged?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    private void Sync()
    {
        var hasRepo = _workspace?.Focused is not null;
        if (_side == ShortcutsBarSide.Left)
        {
            if (_branchBtn is not null) _branchBtn.IsEnabled = hasRepo;
            if (_terminalBtn is not null) _terminalBtn.IsEnabled = hasRepo;
        }
        else
        {
            if (_fetchBtn is not null) _fetchBtn.IsEnabled = hasRepo;
            if (_pullBtn is not null) _pullBtn.IsEnabled = hasRepo;
            if (_pushBtn is not null) _pushBtn.IsEnabled = hasRepo;
            if (_stashBtn is not null) _stashBtn.IsEnabled = hasRepo;
        }
    }

    private static Button IconTextButton(string icon, string? label, string tip, Action onClick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(IconGlyphPath(icon, 18, "ColorTextMuted"));
        if (!string.IsNullOrEmpty(label))
            content.Children.Add(new TextBlock { Text = label, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button
        {
            Content = content,
            Padding = new Thickness(7, 2),
            Background = Brushes.Transparent,
            BorderThickness = default,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        b.Classes.Add("flat");
        ToolTip.SetTip(b, tip);
        b.Click += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    private async Task CreateBranchAsync()
    {
        if (_workspace?.Focused is not { } ctx || _dialogs is null)
            return;
        var name = await _dialogs.PromptAsync("New branch", "Branch name:");
        if (string.IsNullOrWhiteSpace(name))
            return;
        name = name.Trim();

        var selected = ctx.State.SelectedCommitId;
        var startPoint = selected?.ToString() ?? ctx.State.CurrentBranch?.CanonicalName ?? "HEAD";
        try
        {
            await ctx.Gateway.CreateBranchAsync(name, startPoint, checkout: false, ctx.Lifetime);
            await ctx.State.RefreshAllAsync(ctx.Lifetime);
            _notifications?.Publish(NotificationSeverity.Success,
                selected is { } id ? $"Created {name} at {id.ToShortString(7)}." : $"Created {name}.");
        }
        catch (Exception ex)
        {
            _notifications?.Publish(NotificationSeverity.Error,
                $"Could not create branch: {ex.Message}", ex.NotificationDetails());
        }
    }

    private async Task StashAllAsync()
    {
        if (_workspace?.Focused is not { } ctx || _stash is null || _settings is null || _dialogs is null || _notifications is null)
            return;
        if (_stashBtn is not null)
            _stashBtn.IsEnabled = false;
        try
        {
            await StashActions.StashAllAsync(ctx, _stash, _settings, _dialogs, _notifications);
        }
        finally
        {
            if (_stashBtn is not null)
                _stashBtn.IsEnabled = _workspace?.Focused is not null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _container.LayoutUpdated -= OnPanelLayoutUpdated;
        if (_workspace is not null)
            _workspace.FocusedRepositoryChanged -= OnFocusChanged;
    }
}
