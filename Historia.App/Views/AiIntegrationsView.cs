namespace Historia.Views;

/// <summary>
/// The AI Integrations window: the numbered profile list on the left, the selected profile's
/// editor on the right. Shown in the centre pane while <see cref="IAiIntegrationsPanel.IsOpen"/>.
/// Every field commits on LostFocus (and Enter for single-line boxes). The editor is built once per
/// selected profile and is never rebuilt by its own edits, so a commit can't destroy the field
/// that Tab just moved focus to; a change that came from elsewhere is applied field by field,
/// skipping any field the user is typing in. Models are fetched only when the user opens the
/// Model list or presses refresh.
/// </summary>
public sealed class AiIntegrationsView : UserControl, IDisposable
{
    private static readonly string[] DiffLimitLabels = ["Unlimited", "64 KB", "128 KB", "192 KB", "256 KB", "320 KB"];

    private readonly IAiProfileService _profiles;
    private readonly IAiIntegrationsPanel _panel;
    private readonly IDialogService _dialogs;
    private readonly IAiModelCatalog _catalog;
    private readonly INotificationService? _notifications;
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly ContentControl _editorHost = new();
    private readonly Button _addButton;
    private readonly Button _deleteButton;
    private readonly Button _closeButton;
    private readonly List<ProfileRow> _rows = [];
    private readonly EventHandler _onProfilesChanged;
    private readonly EventHandler _onPanelChanged;
    private readonly EventHandler<string> _onCatalogChanged;
    private string? _selectedId;
    private Editor? _editor;
    private CancellationTokenSource? _modelsCts;
    private bool _disposed;

    internal sealed record ProfileRow(string Id, Border Row, TextBlock Label, Button Check);

    internal sealed class Editor(string id)
    {
        public string Id { get; } = id;
        public AiServiceProfile Snapshot { get; set; } = new();
        public bool Syncing { get; set; }
        public List<Action<AiServiceProfile>> Syncs { get; } = [];

        public Button Active { get; init; } = null!;
        public ComboBox Template { get; init; } = null!;
        public TextBox Name { get; init; } = null!;
        public TextBox Server { get; init; } = null!;
        public TextBox Key { get; init; } = null!;
        public Border KeyFrame { get; init; } = null!;
        public CheckBox EnvKey { get; init; } = null!;
        public ComboBox Model { get; init; } = null!;
        public Button ModelRefresh { get; set; } = null!;
        public TextBlock ModelNote { get; init; } = null!;
        public CheckBox AutoFetch { get; init; } = null!;
        public ComboBox Effort { get; init; } = null!;
        public ComboBox DiffLimit { get; init; } = null!;
        public TextBox Prompt { get; init; } = null!;
        public TextBox Headers { get; init; } = null!;
    }

    internal IReadOnlyList<ProfileRow> RowsForTesting => _rows;
    internal Editor? EditorForTesting => _editor;
    internal string? SelectedIdForTesting => _selectedId;
    internal Button AddButtonForTesting => _addButton;
    internal Button DeleteButtonForTesting => _deleteButton;
    internal Button CloseButtonForTesting => _closeButton;

    /// <summary>Completes the model fetch the last open/refresh started; for tests.</summary>
    internal Task ModelFetchForTesting { get; private set; } = Task.CompletedTask;

    public AiIntegrationsView(IServiceProvider sp)
    {
        _profiles = sp.GetRequiredService<IAiProfileService>();
        _panel = sp.GetRequiredService<IAiIntegrationsPanel>();
        _dialogs = sp.GetRequiredService<IDialogService>();
        _catalog = sp.GetRequiredService<IAiModelCatalog>();
        _notifications = sp.GetService<INotificationService>();

        this.Dyn(BackgroundProperty, "ColorBg");
        this.AddHandler(PointerPressedEvent, SettingsView.OnRootPointerPressedTunnel, RoutingStrategies.Tunnel);

        var title = Text("AI Integrations", "ColorText", 14, FontWeight.SemiBold);
        _closeButton = TextButton("X", _panel.Close);
        ToolTip.SetTip(_closeButton, "Close");
        Avalonia.Automation.AutomationProperties.SetName(_closeButton, "Close");
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Height = 30,
            Margin = new Thickness(12, 4),
        };
        header.Children.Add(title);
        Grid.SetColumn(_closeButton, 1);
        header.Children.Add(_closeButton);

        _addButton = IconButton("add", "Add an AI integration", () => _ = AddAsync(), 16);
        _deleteButton = IconButton("delete", "Delete the selected AI integration", () => _ = DeleteSelectedAsync(), 16);
        var bottomBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 6) };
        bottomBar.Children.Add(_addButton);
        bottomBar.Children.Add(_deleteButton);

        var left = new DockPanel { Width = 260 };
        DockPanel.SetDock(bottomBar, Dock.Bottom);
        left.Children.Add(bottomBar);
        left.Children.Add(new ScrollViewer { Content = _list, Padding = new Thickness(8, 4) });
        var leftFrame = new Border { Child = left, BorderThickness = new Thickness(0, 0, 1, 0) };
        leftFrame.Dyn(Border.BorderBrushProperty, "ColorBorder");

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        body.Children.Add(leftFrame);
        Grid.SetColumn(_editorHost, 1);
        body.Children.Add(_editorHost);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(body);
        Content = root;

        var focus = _panel.FocusProfileId;
        _selectedId = _profiles.Profiles.Any(p => p.Id == focus) ? focus : _profiles.Profiles.FirstOrDefault()?.Id;
        RenderList();
        BuildEditor();

        _onProfilesChanged = (_, _) => Dispatcher.UIThread.Post(OnProfilesChanged);
        _onPanelChanged = (_, _) => Dispatcher.UIThread.Post(OnPanelChanged);
        _onCatalogChanged = (_, id) => Dispatcher.UIThread.Post(() => OnCatalogChanged(id));
        _profiles.Changed += _onProfilesChanged;
        _panel.OpenChanged += _onPanelChanged;
        _catalog.Changed += _onCatalogChanged;
    }

    // ── Profile list ──

    private AiServiceProfile? Find(string? id) => id is null ? null : _profiles.Profiles.FirstOrDefault(p => p.Id == id);

    private void RenderList()
    {
        _list.Children.Clear();
        _rows.Clear();
        var active = _profiles.ActiveProfile?.Id;

        foreach (var profile in _profiles.Profiles)
        {
            var id = profile.Id;
            var isActive = id == active;

            var label = Text(_profiles.DisplayLabel(profile), "ColorText", 13);
            var check = new Button
            {
                Content = Icon("check_circle", 18, isActive ? "ColorSuccess" : "ColorTextFaint"),
                Padding = new Thickness(2),
                Background = Brushes.Transparent,
                BorderThickness = default,
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(check, isActive ? "Active" : "Make active");
            Avalonia.Automation.AutomationProperties.SetName(check, isActive ? "Active" : "Make active");
            check.Click += (_, _) => _ = ToggleActiveAsync(id);

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(label);
            Grid.SetColumn(check, 1);
            grid.Children.Add(check);

            var row = new Border
            {
                Child = grid,
                Padding = new Thickness(8, 6),
                CornerRadius = new CornerRadius(4),
                Cursor = new Cursor(StandardCursorType.Hand),
                Background = Brushes.Transparent,
            };
            if (id == _selectedId)
                row.Dyn(Border.BackgroundProperty, "ColorSurface2");
            row.PointerPressed += (_, _) => Select(id);

            _list.Children.Add(row);
            _rows.Add(new ProfileRow(id, row, label, check));
        }

        _deleteButton.IsEnabled = Find(_selectedId) is not null;
        RefreshActiveButton();
    }

    private void Select(string? id)
    {
        if (id == _selectedId && _editor is not null)
            return;
        _selectedId = id;
        RenderList();
        BuildEditor();
    }

    private async Task AddAsync()
    {
        var profile = await _profiles.AddAsync();
        Select(profile.Id);
    }

    private async Task DeleteSelectedAsync()
    {
        var profile = Find(_selectedId);
        if (profile is null)
            return;

        var label = _profiles.DisplayLabel(profile);
        var wasActive = _profiles.ActiveProfile?.Id == profile.Id;
        var effect = $"Delete '{label}' and its saved API key?"
            + (wasActive ? " It is the active integration, so AI commit messages will be turned off." : "")
            + " This can't be undone.";
        if (!await _dialogs.ConfirmDestructiveAsync("Delete AI integration", effect, "Delete"))
            return;

        await _profiles.DeleteAiServiceAsync(profile.Id);
        Select(_profiles.Profiles.FirstOrDefault()?.Id);
    }

    private async Task ToggleActiveAsync(string id)
    {
        var profile = Find(id);
        if (profile is null)
            return;

        var active = _profiles.ActiveProfile;
        if (active?.Id == id)
        {
            await _profiles.SetActiveAsync(null);
            return;
        }
        if (active is null)
        {
            await _profiles.SetActiveAsync(id);
            return;
        }

        var choice = await _dialogs.ChooseAsync(
            "Switch AI integration",
            $"'{_profiles.DisplayLabel(active)}' is the active AI integration. Switch to '{_profiles.DisplayLabel(profile)}'?",
            ["Save"],
            defaultIndex: 0);
        if (choice == 0)
            await _profiles.SetActiveAsync(id);
    }

    // ── Editor ──

    private void BuildEditor()
    {
        _modelsCts?.Cancel();
        _modelsCts = null;
        _editor = null;

        var profile = Find(_selectedId);
        if (profile is null)
        {
            _editorHost.Content = new TextBlock
            {
                Text = "Add an AI integration with + to get started.",
                Margin = new Thickness(24),
            }.Dyn(TextBlock.ForegroundProperty, "ColorTextMuted");
            return;
        }

        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(24, 12), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };

        var activeBtn = new Button
        {
            Content = "Active",
            FontSize = 13,
            Padding = new Thickness(8, 2),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        activeBtn.Click += (_, _) => _ = ToggleActiveAsync(profile.Id);

        var topRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(activeBtn, 1);
        topRow.Children.Add(activeBtn);

        var template = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        template.Items.Add("Custom (default)");
        template.Items.Add("Gemini");

        var name = new TextBox();
        var server = new TextBox();
        var key = new TextBox { PasswordChar = '•' };
        var keyFrame = new Border { Child = key, CornerRadius = new CornerRadius(4) };
        var envKey = new CheckBox { Content = "Entered value is the name to load API key from ENV" };
        var model = new ComboBox { IsEditable = true, HorizontalAlignment = HorizontalAlignment.Stretch };
        var modelNote = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false }
            .Dyn(TextBlock.ForegroundProperty, "ColorWarning");
        var autoFetch = new CheckBox { Content = "Fetch available model-ids automatically" };
        var effort = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var level in AiReasoningEffort.Levels)
            effort.Items.Add(level);
        var diffLimit = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var label in DiffLimitLabels)
            diffLimit.Items.Add(label);
        var prompt = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 80,
            Watermark = "Use `-` to list your requirements",
        };
        var headers = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 60,
            Watermark = "One `Name: Value` per line",
        };

        var e = new Editor(profile.Id)
        {
            Active = activeBtn,
            Template = template,
            Name = name,
            Server = server,
            Key = key,
            KeyFrame = keyFrame,
            EnvKey = envKey,
            Model = model,
            ModelNote = modelNote,
            AutoFetch = autoFetch,
            Effort = effort,
            DiffLimit = diffLimit,
            Prompt = prompt,
            Headers = headers,
        };
        e.Snapshot = profile;
        _editor = e;

        // Text boxes: commit on LostFocus, and on Enter for the single-line ones.
        WireText(e, name, p => p.Name, (p, v) => p with { Name = v }, singleLine: true);
        WireText(e, server, p => p.Server, (p, v) => p with { Server = v }, singleLine: true);
        WireText(e, key, p => p.ApiKey, (p, v) => p with { ApiKey = v }, singleLine: true);
        WireText(e, prompt, p => p.AdditionalPrompt, (p, v) => p with { AdditionalPrompt = v }, singleLine: false);
        WireText(e, headers, p => p.ExtraHeaders, (p, v) => p with { ExtraHeaders = v }, singleLine: false);

        WireCheck(e, envKey, p => p.ReadApiKeyFromEnv, (p, v) => p with { ReadApiKeyFromEnv = v });
        WireCheck(e, autoFetch, p => p.AutoFetchModels, (p, v) => p with { AutoFetchModels = v });

        WireCombo(e, effort, p => Math.Max(0, AiReasoningEffort.Levels.ToList().FindIndex(l => l == p.ReasoningEffort)),
            (p, i) => p with { ReasoningEffort = AiReasoningEffort.Levels[i] });
        WireCombo(e, diffLimit, p => Math.Clamp(p.DiffCapUnits, 0, AiDiffCap.MaxUnits),
            (p, i) => p with { DiffCapUnits = i });

        e.Syncs.Add(p =>
        {
            e.Syncing = true;
            template.SelectedIndex = p.Kind == AiServiceKind.Gemini ? 1 : 0;
            e.Syncing = false;
        });
        template.SelectionChanged += (_, _) =>
        {
            if (!e.Syncing)
                _ = OnTemplateChangedAsync(e);
        };

        // The key is masked unless it holds an environment variable's name.
        e.Syncs.Add(_ => UpdateKeyFrame(e));

        // Model: the editable text commits on LostFocus; picking an item commits at once.
        e.Syncs.Add(p => SetModelText(e, p.Model));
        model.SelectionChanged += (_, _) =>
        {
            if (!e.Syncing && model.SelectedItem is string picked && picked != e.Snapshot.Model)
            {
                _ = Commit(e, p => p with { Model = picked });
            }
        };
        model.LostFocus += (_, _) =>
        {
            if (e.Syncing)
                return;
            var typed = (model.Text ?? "").Trim();
            if (typed != e.Snapshot.Model)
                _ = Commit(e, p => p with { Model = typed });
        };
        model.DropDownOpened += (_, _) => OnModelListOpened(e);
        var refresh = IconButton("refresh", "Refresh the model list", () => ModelFetchForTesting = FetchModelsAsync(e), 16);
        e.ModelRefresh = refresh;

        var modelRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        modelRow.Children.Add(model);
        Grid.SetColumn(refresh, 1);
        modelRow.Children.Add(refresh);

        panel.Children.Add(topRow);
        panel.Children.Add(Field("Template", template));
        panel.Children.Add(Field("Name", name));
        panel.Children.Add(Field("Server", server));
        panel.Children.Add(Field("API Key", keyFrame));
        panel.Children.Add(envKey);
        panel.Children.Add(Field("Model", modelRow));
        panel.Children.Add(modelNote);
        panel.Children.Add(autoFetch);
        panel.Children.Add(Field("Reasoning Effort Level", effort));
        panel.Children.Add(Field("Diff size limit", diffLimit));
        panel.Children.Add(Field("Additional Prompt", prompt));
        panel.Children.Add(Field("Extra Headers", headers));
        panel.Children.Add(Text("Your staged changes are sent to this service only when you click Generate.", "ColorTextMuted", 12));

        var cached = _catalog.Get(profile);
        if (cached is not null)
        {
            e.Syncing = true;
            model.ItemsSource = cached;
            e.Syncing = false;
        }

        foreach (var sync in e.Syncs)
            sync(profile);

        RefreshActiveButton();
        _editorHost.Content = new ScrollViewer { Content = panel };
    }

    private static Control Field(string label, Control control)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(Text(label, "ColorTextMuted", 12));
        stack.Children.Add(control);
        return stack;
    }

    private void WireText(Editor e, TextBox box, Func<AiServiceProfile, string> read,
        Func<AiServiceProfile, string, AiServiceProfile> write, bool singleLine)
    {
        void CommitIfChanged()
        {
            if (e.Syncing || _editor != e)
                return;
            var text = box.Text ?? "";
            if (text != read(e.Snapshot))
                _ = Commit(e, p => write(p, text));
        }

        box.LostFocus += (_, _) => CommitIfChanged();
        if (singleLine)
        {
            box.KeyDown += (_, args) =>
            {
                if (args.Key == Key.Enter)
                    CommitIfChanged();
            };
        }
        e.Syncs.Add(p =>
        {
            var value = read(p);
            if ((box.Text ?? "") == value || box.IsKeyboardFocusWithin)
                return;
            e.Syncing = true;
            box.Text = value;
            e.Syncing = false;
        });
    }

    private void WireCheck(Editor e, CheckBox box, Func<AiServiceProfile, bool> read,
        Func<AiServiceProfile, bool, AiServiceProfile> write)
    {
        box.IsCheckedChanged += (_, _) =>
        {
            if (e.Syncing || _editor != e)
                return;
            var value = box.IsChecked == true;
            if (value != read(e.Snapshot))
                _ = Commit(e, p => write(p, value));
        };
        e.Syncs.Add(p =>
        {
            e.Syncing = true;
            box.IsChecked = read(p);
            e.Syncing = false;
        });
    }

    private void WireCombo(Editor e, ComboBox box, Func<AiServiceProfile, int> read,
        Func<AiServiceProfile, int, AiServiceProfile> write)
    {
        box.SelectionChanged += (_, _) =>
        {
            if (e.Syncing || _editor != e || box.SelectedIndex < 0)
                return;
            var index = box.SelectedIndex;
            if (index != read(e.Snapshot))
                _ = Commit(e, p => write(p, index));
        };
        e.Syncs.Add(p =>
        {
            e.Syncing = true;
            box.SelectedIndex = read(p);
            e.Syncing = false;
        });
    }

    /// <summary>Saves a change, records it as the editor's snapshot (so the change event it
    /// raises is recognised as the editor's own), and refreshes the list and the key frame.
    /// A new edit clears the "switched model" note.</summary>
    private Task Commit(Editor e, Func<AiServiceProfile, AiServiceProfile> mutate)
    {
        e.ModelNote.IsVisible = false;
        var task = _profiles.UpdateAsync(e.Id, mutate);
        if (Find(e.Id) is { } saved)
            e.Snapshot = saved;
        RenderList();
        UpdateKeyFrame(e);
        return task;
    }

    private void UpdateKeyFrame(Editor e)
    {
        e.Key.RevealPassword = e.Snapshot.ReadApiKeyFromEnv;
        var missing = !_profiles.HasApiKey(e.Snapshot);
        e.KeyFrame.BorderThickness = new Thickness(missing ? 2 : 0);
        if (missing)
            e.KeyFrame.Dyn(Border.BorderBrushProperty, "ColorDanger");
        else
            e.KeyFrame.ClearValue(Border.BorderBrushProperty);
    }

    private void SetModelText(Editor e, string model)
    {
        if (e.Model.IsKeyboardFocusWithin && e.Model.Text == model)
            return;
        e.Syncing = true;
        e.Model.SelectedItem = e.Model.Items.OfType<string>().Contains(model) ? model : null;
        e.Model.Text = model;
        e.Syncing = false;
    }

    private async Task OnTemplateChangedAsync(Editor e)
    {
        var kind = e.Template.SelectedIndex == 1 ? AiServiceKind.Gemini : AiServiceKind.Custom;
        if (kind == e.Snapshot.Kind)
            return;

        if (AiServiceTemplates.WouldOverwrite(e.Snapshot, kind)
            && !await _dialogs.ConfirmAsync("Use the Gemini template", "Fill in Gemini's connection? Replaces Server and Model.", confirmLabel: "Replace"))
        {
            e.Syncing = true;
            e.Template.SelectedIndex = e.Snapshot.Kind == AiServiceKind.Gemini ? 1 : 0;
            e.Syncing = false;
            return;
        }
        if (_editor != e)
            return;

        await Commit(e, p => kind == AiServiceKind.Gemini ? AiServiceTemplates.ApplyGemini(p) : AiServiceTemplates.ApplyCustom(p));
        SyncEditor(e);
    }

    private void SyncEditor(Editor e)
    {
        foreach (var sync in e.Syncs)
            sync(e.Snapshot);
    }

    // ── Models ──

    private void OnModelListOpened(Editor e)
    {
        if (_editor != e)
            return;
        var cached = _catalog.Get(e.Snapshot);
        if (cached is not null)
        {
            ModelFetchForTesting = SwitchMissingGeminiModelAsync(e, cached);
        }
        else if (e.Snapshot.AutoFetchModels)
        {
            ModelFetchForTesting = FetchModelsAsync(e);
        }
    }

    internal void OpenModelListForTesting() => OnModelListOpened(_editor!);

    private void RefreshActiveButton()
    {
        if (_editor is not { } e)
            return;
        var active = _profiles.ActiveProfile?.Id == e.Id;
        var b = e.Active;
        if (active)
        {
            b.ClearValue(Button.ForegroundProperty);
            b.ClearValue(Button.BorderBrushProperty);
            b.ClearValue(Button.BackgroundProperty);
            if (!b.Classes.Contains("success"))
                b.Classes.Add("success");
        }
        else
        {
            b.Classes.Remove("success");
            b.Dyn(Button.ForegroundProperty, "ColorTextFaint");
            b.Dyn(Button.BorderBrushProperty, "ColorBorder");
            b.Background = Brushes.Transparent;
        }

        var tip = active ? "Active" : "Make active";
        ToolTip.SetTip(b, tip);
        Avalonia.Automation.AutomationProperties.SetName(b, tip);
    }

    private void OnCatalogChanged(string id)
    {
        if (_disposed || _editor is not { } e || e.Id != id)
            return;
        var list = _catalog.Get(e.Snapshot);
        if (list is not null && !ItemsEqual(e.Model.ItemsSource as IEnumerable<string>, list))
        {
            e.Syncing = true;
            e.Model.ItemsSource = list;
            e.Syncing = false;
            SetModelText(e, e.Snapshot.Model);
        }
    }

    private static bool ItemsEqual(IEnumerable<string>? current, IReadOnlyList<string> next) =>
        current is not null && current.SequenceEqual(next);

    private Task FetchModelsAsync(Editor e)
    {
        var profile = e.Snapshot;
        if (string.IsNullOrWhiteSpace(profile.Server))
        {
            _notifications?.Publish(NotificationSeverity.Warning, "Set the Server before loading models.");
            return Task.CompletedTask;
        }
        return LoadModelsAsync(e, profile);
    }

    private async Task LoadModelsAsync(Editor e, AiServiceProfile profile)
    {
        _modelsCts?.Cancel();
        var cts = _modelsCts = new CancellationTokenSource();

        e.Model.IsEnabled = false;
        e.Model.PlaceholderText = "Loading…";
        try
        {
            var ids = await _catalog.LoadAsync(profile, cts.Token);
            if (cts.IsCancellationRequested || _disposed || _editor != e)
                return;

            e.Syncing = true;
            e.Model.ItemsSource = ids;
            e.Syncing = false;
            await SwitchMissingGeminiModelAsync(e, ids);
            SetModelText(e, e.Snapshot.Model);
        }
        catch (OperationCanceledException)
        {
        }
        catch (AiException ex)
        {
            if (!_disposed && _editor == e)
                PublishError(_notifications, ex, "Couldn't load the model list");
        }
        catch (Exception ex)
        {
            if (!_disposed && _editor == e)
                _notifications?.Publish(NotificationSeverity.Error, ex.NotificationMessage("Couldn't load the model list"), ex.NotificationDetails());
        }
        finally
        {
            if (_editor == e)
            {
                e.Model.IsEnabled = true;
                e.Model.PlaceholderText = null;
            }
        }
    }

    /// <summary>A Gemini profile whose saved model isn't in the list (or has none) takes the best
    /// replacement; Custom profiles never switch on their own.</summary>
    private async Task SwitchMissingGeminiModelAsync(Editor e, IReadOnlyList<string> ids)
    {
        var profile = e.Snapshot;
        if (profile.Kind != AiServiceKind.Gemini)
            return;

        var saved = profile.Model;
        var blank = string.IsNullOrWhiteSpace(saved);
        if (!blank && ids.Contains(saved))
            return;

        var pick = GeminiModelSelector.PickReplacement(ids, blank ? null : saved);
        if (pick is null)
            return;

        await Commit(e, p => p with { Model = pick });
        SetModelText(e, pick);
        if (!blank)
        {
            e.ModelNote.Text = $"'{saved}' is no longer available — switched to '{pick}'.";
            e.ModelNote.IsVisible = true;
        }
    }

    internal static void PublishError(INotificationService? notifications, AiException ex, string? prefix = null)
    {
        var message = prefix is null ? ex.Message : $"{prefix}: {ex.Message}";
        notifications?.Publish(NotificationSeverity.Error, message, ex.Details);
    }

    // ── Events ──

    private void OnProfilesChanged()
    {
        if (_disposed)
            return;

        if (Find(_selectedId) is null)
        {
            _selectedId = _profiles.Profiles.FirstOrDefault()?.Id;
            RenderList();
            BuildEditor();
            return;
        }

        RenderList();
        if (_editor is { } e && Find(e.Id) is { } current && current != e.Snapshot)
        {
            e.Snapshot = current;
            SyncEditor(e);
        }
    }

    private void OnPanelChanged()
    {
        if (_disposed || !_panel.IsOpen)
            return;
        if (_panel.FocusProfileId is { } id && Find(id) is not null)
            Select(id);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _profiles.Changed -= _onProfilesChanged;
        _panel.OpenChanged -= _onPanelChanged;
        _catalog.Changed -= _onCatalogChanged;
        _modelsCts?.Cancel();
        _modelsCts = null;
    }
}
