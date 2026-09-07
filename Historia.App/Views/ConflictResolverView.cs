namespace Historia.Views;

/// <summary>
/// Merge / rebase / cherry-pick conflict resolver (§8.4) — V4 port of ConflictResolver.razor.
/// Per-file: take ours / take theirs / edit the merged text / external tool / mark resolved;
/// then continue or abort the operation.
/// </summary>
public sealed class ConflictResolverView : UserControl, IDisposable
{
    private readonly IServiceProvider _sp;
    private readonly IConflictService _conflicts;
    private readonly INotificationService _notifications;
    private readonly IDialogService _dialogs;
    private readonly RepositoryContext _ctx;

    private readonly StackPanel _list = new() { Spacing = 8, Margin = new Thickness(12) };
    private readonly TextBlock _header = Text("", "ColorText", 14, FontWeight.SemiBold);

    public ConflictResolverView(IServiceProvider sp, RepositoryContext ctx)
    {
        _sp = sp;
        _ctx = ctx;
        _conflicts = sp.GetRequiredService<IConflictService>();
        _notifications = sp.GetRequiredService<INotificationService>();
        _dialogs = sp.GetRequiredService<IDialogService>();

        var top = new StackPanel { Spacing = 6, Margin = new Thickness(12, 8) };
        top.Children.Add(_header);
        var ops = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        ops.Children.Add(TextButton("Continue", () => _ = ContinueAsync(), primary: true));
        ops.Children.Add(TextButton("Abort", () => _ = AbortAsync()));
        top.Children.Add(ops);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(new ScrollViewer { Content = _list });
        Content = root;
        this.Dyn(BackgroundProperty, "ColorChrome");

        _ctx.State.StatusChanged += OnStatusChanged;
        _ = LoadAsync();
    }

    private void OnStatusChanged(object? s, Historia.Services.State.StatusChangedEventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = LoadAsync());

    private async Task LoadAsync()
    {
        try
        {
            var files = await _conflicts.GetConflictsAsync(_ctx, _ctx.Lifetime);
            _list.Children.Clear();
            _header.Text = files.Count == 0
                ? "No conflicts — you can continue the operation."
                : $"{files.Count} conflicted file(s)";
            foreach (var f in files)
            {
                var file = f;
                var card = new StackPanel { Spacing = 6 };
                card.Children.Add(Text($"{f.FilePath}   ({f.Status})", "ColorText", 12, FontWeight.SemiBold));

                var editor = new TextBox
                {
                    AcceptsReturn = true,
                    MinHeight = 120,
                    FontFamily = new FontFamily("Consolas, monospace"),
                    FontSize = 12,
                    Text = f.MergedContent,
                    IsVisible = !f.IsBinary,
                };
                card.Children.Add(editor);

                var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                acts.Children.Add(TextButton("Take ours", () => _ = ResolveSideAsync(file.FilePath, ConflictSide.Ours)));
                acts.Children.Add(TextButton("Take theirs", () => _ = ResolveSideAsync(file.FilePath, ConflictSide.Theirs)));
                if (!f.IsBinary)
                    acts.Children.Add(TextButton("Mark resolved (edited)", () => _ = MarkResolvedAsync(file.FilePath, editor.Text ?? "")));
                acts.Children.Add(TextButton("External merge tool", () => _ = ExternalToolAsync(file.FilePath)));
                card.Children.Add(acts);

                _list.Children.Add(Card(card, 10));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _notifications.Publish(NotificationSeverity.Error, ex.Message, ex.NotificationDetails()); }
    }

    private async Task ResolveSideAsync(string path, ConflictSide side)
        => await Guard(() => _conflicts.ResolveWithSideAsync(_ctx, path, side, _ctx.Lifetime), $"Take {side}");

    private async Task MarkResolvedAsync(string path, string content)
        => await Guard(() => _conflicts.MarkResolvedAsync(_ctx, path, content, _ctx.Lifetime), "Mark resolved");

    private async Task ExternalToolAsync(string path)
    {
        try
        {
            if (!await _conflicts.LaunchExternalMergeToolAsync(_ctx, path, _ctx.Lifetime))
                _notifications.Publish(NotificationSeverity.Warning, "No external merge tool is configured (git config merge.tool).");
        }
        catch (Exception ex) { _notifications.Publish(NotificationSeverity.Error, ex.Message, ex.NotificationDetails()); }
    }

    private async Task ContinueAsync()
        => await Guard(() => _conflicts.ContinueOperationAsync(_ctx, _ctx.Lifetime), "Continue");

    private async Task AbortAsync()
    {
        if (!await _dialogs.ConfirmAsync("Abort operation", "Abort the in-progress merge / rebase / cherry-pick?", destructive: true, "Abort"))
            return;
        await Guard(() => _conflicts.AbortOperationAsync(_ctx, _ctx.Lifetime), "Abort");
    }

    private async Task Guard(Func<Task> op, string what)
    {
        try
        {
            await op();
            await _ctx.State.RefreshAllAsync(_ctx.Lifetime);
        }
        catch (Exception ex) { _notifications.Publish(NotificationSeverity.Error, $"{what} failed: {ex.Message}", ex.NotificationDetails()); }
    }

    public void Dispose() => _ctx.State.StatusChanged -= OnStatusChanged;
}
