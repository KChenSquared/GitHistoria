namespace Historia.Views;

/// <summary>Per-repository settings.</summary>
public sealed class RepoSettingsView : UserControl, IDisposable
{
    private readonly IServiceProvider _sp;
    private readonly IWorkspaceService _workspace;
    private readonly Historia.Services.Settings.IRepoLayoutService _repoLayout;
    private readonly IProfileService _profiles;
    private readonly INavigationBus? _nav;
    private readonly INotificationService? _notifications;
    private readonly StackPanel _root = new() { Spacing = 12, Margin = new Thickness(32), MaxWidth = 560 };
    private readonly Panel _overlay = new() { IsHitTestVisible = false };

    private readonly EventHandler<Historia.Services.Settings.RepoLayoutChangedEventArgs> _onLayout;
    private readonly EventHandler _onProfilesChanged;
    private bool _syncingSshOnly;

    // ── column-row drag reorder (mirrors the app-band tab reorder: live preview + ghost) ──
    private const double DragThreshold = 4;
    private readonly List<Border> _columnRows = [];
    private int _columnsBaseIndex;
    private Border? _dragRow;
    private bool _dragging;
    private Point _pressPos;
    private Point _grabOffset;
    private Historia.Controls.AppBandDragGhost? _ghost;

    public RepoSettingsView(IServiceProvider sp)
    {
        _sp = sp;
        _workspace = sp.GetRequiredService<IWorkspaceService>();
        _repoLayout = sp.GetRequiredService<Historia.Services.Settings.IRepoLayoutService>();
        _profiles = sp.GetRequiredService<IProfileService>();
        _nav = sp.GetService<INavigationBus>();
        _notifications = sp.GetService<INotificationService>();
        _onLayout = (_, _) =>
        {
            if (!_syncingSshOnly)
                Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
        };
        _onProfilesChanged = (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
        Content = new Panel { Children = { new ScrollViewer { Content = _root }, _overlay } };
        this.Dyn(BackgroundProperty, "ColorChrome");
        _root.AddHandler(PointerMovedEvent, OnRowsPointerMoved, RoutingStrategies.Bubble);
        _root.AddHandler(PointerReleasedEvent, OnRowsPointerReleased, RoutingStrategies.Bubble);
        _root.PointerCaptureLost += OnRowsCaptureLost;
        _workspace.FocusedRepositoryChanged += OnFocusChanged;
        _repoLayout.Changed += _onLayout;
        _profiles.ProfilesChanged += _onProfilesChanged;
        Rebuild();
    }

    private void OnFocusChanged(object? s, FocusedRepositoryChangedEventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);

    private void Rebuild()
    {
        _root.Children.Clear();
        var ctx = _workspace.Focused;

        _root.Children.Add(Text(ctx?.DisplayName ?? "Repository settings", "ColorText", 22, FontWeight.SemiBold));
        if (ctx is not null)
            _root.Children.Add(Text(ctx.RepositoryPath, "ColorTextMuted", 12));

        if (ctx is not null)
        {
            // ── Identity ──
            _root.Children.Add(WithHint(Text("Identity", "ColorTextMuted", 13, FontWeight.SemiBold),
                "Name, email and signing key used for commits in this repository."));

            var key = ctx.IdentityKey;
            string label;
            if (key is { } k && _profiles.IsLocalConfigSelected(k))
            {
                label = "Local config";
            }
            else if (ctx.State.ActiveProfile is { } p)
            {
                // stale rendering: a row that names a missing profile shows "<name> (unavailable)".
                label = _profiles.GetProfileById(p.Id) is null
                    ? $"{p.DisplayName} (unavailable)"
                    : p.IsGlobal ? "Local config" : p.DisplayName;
            }
            else
            {
                label = "Local config";
            }

            var profileBtn = new Button
            {
                Content = label + "  ▾",
                Padding = new Thickness(10, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            profileBtn.Click += (_, _) => OpenProfileMenu(profileBtn, ctx);
            _root.Children.Add(profileBtn);

            // ── Network ──
            _root.Children.Add(Text("Network", "ColorTextMuted", 13, FontWeight.SemiBold));
            var sshOnly = new ToggleSwitch
            {
                Content = "SSH only",
                IsChecked = _repoLayout.Get(ctx.RepositoryPath).SshOnly,
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            sshOnly.IsCheckedChanged += (_, _) => _ = OnSshOnlyToggledAsync(sshOnly.IsChecked == true, ctx);
            _root.Children.Add(WithHint(sshOnly,
                "Fetch, pull and push always use SSH, even for HTTPS remotes. HTTPS is never tried."));
        }

        _root.Children.Add(WithHint(Text("Graph columns", "ColorTextMuted", 13, FontWeight.SemiBold),
            "Order and visibility apply to all repositories. Widths are per repository and reset on restart."));

        // the editor works on the application-wide column set and shows even with no repo.
        var columns = _repoLayout.GlobalColumns;
        var path = ctx?.RepositoryPath ?? "";
        _columnRows.Clear();
        _columnsBaseIndex = _root.Children.Count;

        foreach (var column in columns)
        {
            var col = column;
            var kind = col.Kind;

            var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Height = 30 };
            var rowBorder = new Border { Child = rowGrid, Background = Brushes.Transparent, Tag = kind };
            _columnRows.Add(rowBorder);

            var handle = new Border
            {
                Child = Text("☰", "ColorTextMuted", 15),
                Padding = new Thickness(6, 0),
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.SizeAll),
                VerticalAlignment = VerticalAlignment.Center,
            };
            handle.PointerPressed += (_, e) => OnHandlePressed(rowBorder, e);
            rowGrid.Children.Add(handle);

            var cb = new CheckBox { Content = kind.ToString(), IsChecked = col.Visible, Margin = new Thickness(6, 0, 0, 0) };
            cb.IsCheckedChanged += (_, _) =>
            {
                var vis = cb.IsChecked == true;
                var next = _repoLayout.GlobalColumns.Select(c => c.Kind == kind ? c with { Visible = vis } : c).ToList();
                if (next.Count(c => c.Visible) == 0 || next.Count(c => c.Visible && c.IsFlex) != 1)
                    return; // never zero visible / non-unique flex
                _ = _repoLayout.UpdateGlobalAsync(_ => GraphColumnDefaults.Normalize(next));
            };
            Grid.SetColumn(cb, 1);
            rowGrid.Children.Add(cb);

            _root.Children.Add(rowBorder);
        }

        _root.Children.Add(TextButton("Reset to defaults", () =>
        {
            _ = _repoLayout.UpdateGlobalAsync(_ => GraphColumnDefaults.All);
            if (path.Length > 0)
                _ = _repoLayout.UpdateAsync(path, l => l with
                {
                    Columns = GraphColumnDefaults.All,
                    GraphWidthPinned = false,
                });
        }));
    }

    // ── column-row drag reorder ─────────────────────────────────────────────────────────
    private void OnHandlePressed(Border row, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _dragRow = row;
        _pressPos = e.GetPosition(this);
        _grabOffset = e.GetPosition(row);
        _dragging = false;
        e.Handled = true;
    }

    private void OnRowsPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragRow is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var here = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(here.Y - _pressPos.Y) < DragThreshold)
                return;
            _dragging = true;
            e.Pointer.Capture(_root);
            StartGhost(_dragRow);
            _dragRow.Opacity = 0.3;
        }

        ReorderTo(e.GetPosition(_root).Y);
        _ghost?.MoveTo(new Point(here.X - _grabOffset.X, here.Y - _grabOffset.Y));
    }

    private void OnRowsPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragRow is null)
            return;
        var wasDragging = _dragging;
        var dropped = _dragRow;
        _dragRow = null;
        _dragging = false;
        StopGhost();
        dropped.Opacity = 1;
        e.Pointer.Capture(null);

        if (!wasDragging)
            return;
        e.Handled = true;

        var order = _columnRows.Select(r => (GraphColumnKind)r.Tag!).ToList();
        var byKind = _repoLayout.GlobalColumns.ToDictionary(c => c.Kind);
        var reordered = order.Where(byKind.ContainsKey).Select(k => byKind[k]).ToList();
        _ = _repoLayout.UpdateGlobalAsync(_ => GraphColumnDefaults.Normalize(reordered));
    }

    private void OnRowsCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_dragRow is not null)
            _dragRow.Opacity = 1;
        _dragRow = null;
        _dragging = false;
        StopGhost();
    }

    /// <summary>Live preview: shuffle the row currently under the cursor into place, like the
    /// app-band tab reorder.</summary>
    private void ReorderTo(double y)
    {
        if (_dragRow is null)
            return;
        var cur = _columnRows.IndexOf(_dragRow);
        if (cur < 0)
            return;

        var target = 0;
        for (var i = 0; i < _columnRows.Count; i++)
        {
            var b = _columnRows[i].Bounds;
            if (y < b.Y + b.Height / 2)
                break;
            target = i + 1;
        }
        target = Math.Clamp(target, 0, _columnRows.Count - 1);
        if (target == cur)
            return;

        _columnRows.RemoveAt(cur);
        _columnRows.Insert(target, _dragRow);

        foreach (var r in _columnRows)
            _root.Children.Remove(r);
        for (var i = 0; i < _columnRows.Count; i++)
            _root.Children.Insert(_columnsBaseIndex + i, _columnRows[i]);
    }

    private void StartGhost(Border row)
    {
        if (row.Bounds.Width <= 0 || row.Bounds.Height <= 0)
            return;
        var px = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(row.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(row.Bounds.Height)));
        var rtb = new RenderTargetBitmap(px);
        rtb.Render(row);
        _ghost = new Historia.Controls.AppBandDragGhost(rtb, row.Bounds.Size);
        _overlay.Children.Add(_ghost);
    }

    private void StopGhost()
    {
        if (_ghost is not null)
            _overlay.Children.Remove(_ghost);
        _ghost = null;
    }

    private async Task OnSshOnlyToggledAsync(bool on, RepositoryContext ctx)
    {
        _syncingSshOnly = true;
        try
        {
            await _repoLayout.UpdateAsync(ctx.RepositoryPath, l => l with { SshOnly = on });
        }
        finally
        {
            _syncingSshOnly = false;
        }
    }

    private void OpenProfileMenu(Button anchor, RepositoryContext ctx)
    {
        var key = ctx.IdentityKey;
        var localSelected = key is { } k && _profiles.IsLocalConfigSelected(k);
        var active = ctx.State.ActiveProfile;

        var menu = new MenuFlyout();
        var menuItems = new List<Control>();

        var local = new MenuItem
        {
            Header = "Local config",
            Icon = localSelected ? new TextBlock { Text = "✓" } : null,
        };
        local.Click += (_, _) => _ = SelectLocalConfigAsync(ctx);
        menuItems.Add(local);
        menuItems.Add(new Separator());

        foreach (var p in _profiles.GetAllProfiles())
        {
            var pp = p;
            var mi = new MenuItem
            {
                Header = p.IsGlobal ? "Global (~/.gitconfig)" : p.DisplayName,
                Icon = !localSelected && active is not null && active.Id == p.Id ? new TextBlock { Text = "✓" } : null,
            };
            mi.Click += (_, _) => _ = BindProfileAsync(ctx, pp);
            menuItems.Add(mi);
        }
        menuItems.Add(new Separator());
        var add = new MenuItem { Header = "Add profile…" };
        add.Click += (_, _) => _nav?.RequestNavigate("profiles?intent=add");
        menuItems.Add(add);
        var manage = new MenuItem { Header = "Manage profiles…" };
        manage.Click += (_, _) => _nav?.RequestNavigate("profiles");
        menuItems.Add(manage);

        menu.ItemsSource = menuItems;
        menu.ShowAt(anchor);
    }

    private async Task<RepoIdentityKey> KeyForAsync(RepositoryContext ctx)
        => ctx.IdentityKey ?? await (_sp.GetService<IRepoIdentityService>()?.ResolveAsync(ctx, ctx.Lifetime) ?? Task.FromResult(new RepoIdentityKey("")));

    private async Task BindProfileAsync(RepositoryContext ctx, GitProfile profile)
    {
        try
        {
            var key = await KeyForAsync(ctx);
            await _profiles.BindProfileToRepositoryAsync(key, ctx.RepositoryPath, profile.Id, ctx.Lifetime);
            ctx.State.SetActiveProfile(profile);
            Rebuild();
        }
        catch (Exception ex)
        {
            _notifications?.Publish(NotificationSeverity.Error,
                $"Could not set profile: {ex.Message}", ex.NotificationDetails());
        }
    }

    private async Task SelectLocalConfigAsync(RepositoryContext ctx)
    {
        try
        {
            var key = await KeyForAsync(ctx);
            await _profiles.SetLocalConfigForRepositoryAsync(key, ctx.RepositoryPath, ctx.Lifetime);
            ctx.State.SetActiveProfile(null);
            Rebuild();
        }
        catch (Exception ex)
        {
            _notifications?.Publish(NotificationSeverity.Error,
                $"Could not set profile: {ex.Message}", ex.NotificationDetails());
        }
    }

    public void Dispose()
    {
        _workspace.FocusedRepositoryChanged -= OnFocusChanged;
        _repoLayout.Changed -= _onLayout;
        _profiles.ProfilesChanged -= _onProfilesChanged;
    }
}
