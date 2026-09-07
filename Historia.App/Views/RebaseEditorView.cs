using Historia.Services.Git;
using Historia.Services.PowerTools;
using static Historia.Views.ViewKit;

namespace Historia.Views;

/// <summary>
/// Interactive-rebase todo editor (§8.1) — V4 port of RebaseEditorPage.razor. Reached via the
/// <c>rebase?onto=&lt;ref&gt;</c> route. Reorder + set an action per commit, then Start.
/// </summary>
public sealed class RebaseEditorView : UserControl
{
    private readonly IServiceProvider _sp;
    private readonly IRebaseService _rebase;
    private readonly INotificationService _notifications;
    private readonly INavigationBus _nav;
    private readonly IWorkspaceService _workspace;
    private readonly string _onto;

    private readonly StackPanel _root = new() { Spacing = 8, Margin = new Thickness(24), MaxWidth = 760 };
    private RebaseSession? _session;
    private List<RebaseEntry> _entries = [];
    private bool _autostash = true;

    public RebaseEditorView(IServiceProvider sp, string onto)
    {
        _sp = sp;
        _onto = onto;
        _rebase = sp.GetRequiredService<IRebaseService>();
        _notifications = sp.GetRequiredService<INotificationService>();
        _nav = sp.GetRequiredService<INavigationBus>();
        _workspace = sp.GetRequiredService<IWorkspaceService>();

        Content = new ScrollViewer { Content = _root };
        this.Dyn(BackgroundProperty, "ColorChrome");
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _root.Children.Clear();
        _root.Children.Add(Text("Interactive rebase", "ColorText", 20, FontWeight.SemiBold));

        var repo = _workspace.Focused;
        if (repo is null || string.IsNullOrWhiteSpace(_onto))
        {
            _root.Children.Add(Text("No repository open, or no upstream ref was specified.", "ColorDanger", 13));
            return;
        }

        try
        {
            _session = await _rebase.BuildSessionAsync(repo, _onto, repo.Lifetime);
            _entries = _session.Entries.ToList();
        }
        catch (Exception ex)
        {
            _root.Children.Add(Text($"Could not build the rebase session: {ex.Message}", "ColorDanger", 13));
            return;
        }

        RenderList();
    }

    private void RenderList()
    {
        if (_session is null) return;
        _root.Children.Clear();
        _root.Children.Add(Text("Interactive rebase", "ColorText", 20, FontWeight.SemiBold));
        _root.Children.Add(Text($"{_session.SourceBranchName} onto {_session.OntoRefName} — {_entries.Count} commit(s), oldest first. Squash/fixup melds into the row above.", "ColorTextMuted", 12));

        for (var i = 0; i < _entries.Count; i++)
        {
            var idx = i;
            var e = _entries[idx];
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,120,*"), Margin = new Thickness(0, 2) };

            var up = new Button { Content = "▲", Padding = new Thickness(6, 0), IsEnabled = idx > 0 };
            up.Click += (_, _) => { (_entries[idx - 1], _entries[idx]) = (_entries[idx], _entries[idx - 1]); RenderList(); };
            var down = new Button { Content = "▼", Padding = new Thickness(6, 0), IsEnabled = idx < _entries.Count - 1 };
            down.Click += (_, _) => { (_entries[idx + 1], _entries[idx]) = (_entries[idx], _entries[idx + 1]); RenderList(); };
            var mv = new StackPanel { Orientation = Orientation.Horizontal };
            mv.Children.Add(up); mv.Children.Add(down);
            row.Children.Add(mv);

            var action = new ComboBox { Width = 90, Margin = new Thickness(6, 0) };
            foreach (var a in Enum.GetValues<RebaseAction>())
                action.Items.Add(a);
            action.SelectedItem = e.Action;
            action.SelectionChanged += (_, _) =>
            {
                if (action.SelectedItem is RebaseAction a)
                    _entries[idx] = _entries[idx] with { Action = a };
            };
            Grid.SetColumn(action, 1);
            row.Children.Add(action);

            var sha = Text(e.CommitId.Sha[..8], "ColorTextMuted", 11);
            Grid.SetColumn(sha, 2);
            row.Children.Add(sha);

            var msg = Text(e.MessageShort, "ColorText", 12);
            Grid.SetColumn(msg, 3);
            row.Children.Add(msg);

            _root.Children.Add(row);
        }

        _root.Children.Add(Check("Autostash local changes", _autostash, v => _autostash = v));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(TextButton("Start rebase", () => _ = StartAsync(), primary: true));
        buttons.Children.Add(TextButton("Cancel", () => _nav.RequestNavigate("repo")));
        _root.Children.Add(buttons);
    }

    private static Control Check(string label, bool value, Action<bool> onChange)
    {
        var cb = new CheckBox { Content = label, IsChecked = value };
        cb.IsCheckedChanged += (_, _) => onChange(cb.IsChecked == true);
        return cb;
    }

    private async Task StartAsync()
    {
        if (_session is null) return;
        var repo = _workspace.Focused;
        if (repo is null) return;

        var session = _session with { Entries = _entries };
        var issues = _rebase.Validate(session);
        if (issues.Any(i => !i.RequiresConfirmation))
        {
            _notifications.Publish(NotificationSeverity.Error, string.Join("\n", issues.Select(i => i.Message)));
            return;
        }

        try
        {
            await _rebase.StartAsync(session, repo, _autostash, repo.Lifetime);
            await repo.State.RefreshAllAsync(repo.Lifetime);
            var conflicts = repo.State.Status.Conflicted.Count;
            _notifications.Publish(conflicts > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success,
                conflicts > 0 ? $"Rebase paused on {conflicts} conflict(s)." : "Rebase complete.");
            _nav.RequestNavigate("repo");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, $"Rebase failed: {ex.Message}", ex.NotificationDetails());
        }
    }
}
