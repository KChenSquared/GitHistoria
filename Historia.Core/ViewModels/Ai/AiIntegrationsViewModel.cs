namespace Historia.ViewModels;

/// <summary>
/// The AI Integrations window: the numbered profile list and the editor of the selected profile.
/// The editor is created once per selected profile and is never replaced by its own edits, so a
/// commit can never destroy the field that Tab just moved focus to; a change that comes from
/// elsewhere is applied to it field by field.
/// </summary>
public sealed partial class AiIntegrationsViewModel : ViewModelBase
{
    private readonly IAiProfileService _profiles;
    private readonly IAiIntegrationsPanel _panel;
    private readonly IDialogService _dialogs;
    private readonly IAiModelCatalog _catalog;
    private readonly IUiDispatcher _ui;
    private readonly INotificationService? _notifications;

    public AiIntegrationsViewModel(
        IAiProfileService profiles,
        IAiIntegrationsPanel panel,
        IDialogService dialogs,
        IAiModelCatalog catalog,
        IUiDispatcher ui,
        INotificationService? notifications = null)
    {
        _profiles = profiles;
        _panel = panel;
        _dialogs = dialogs;
        _catalog = catalog;
        _ui = ui;
        _notifications = notifications;

        Rows = [];

        var focus = _panel.FocusProfileId;
        SelectedId = _profiles.Profiles.Any(p => p.Id == focus) ? focus : _profiles.Profiles.FirstOrDefault()?.Id;
        RefreshRows();
        BuildEditor();

        Subscribe(() => _profiles.Changed += OnProfilesChanged, () => _profiles.Changed -= OnProfilesChanged);
        Subscribe(() => _panel.OpenChanged += OnPanelChanged, () => _panel.OpenChanged -= OnPanelChanged);
        Subscribe(() => _catalog.Changed += OnCatalogChanged, () => _catalog.Changed -= OnCatalogChanged);
    }

    /// <summary>The profile list, in the order the profiles were added.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AiProfileRowViewModel> Rows { get; private set; }

    /// <summary>Id of the selected profile, or null when there is none.</summary>
    [ObservableProperty]
    public partial string? SelectedId { get; private set; }

    /// <summary>The editor of the selected profile, or null when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditor))]
    public partial AiProfileEditorViewModel? Editor { get; private set; }

    public bool HasEditor => Editor is not null;

    /// <summary>True while a profile is selected, which is what the Delete button needs.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    public partial bool CanDelete { get; private set; }

    /// <summary>Reports a failed AI request as an error notice, with the service's own detail text.</summary>
    public static void PublishError(INotificationService? notifications, AiException ex, string? prefix = null)
    {
        var message = prefix is null ? ex.Message : $"{prefix}: {ex.Message}";
        notifications?.Publish(NotificationSeverity.Error, message, ex.Details);
    }

    /// <summary>Selects a profile, or none for null, and builds its editor.</summary>
    public void Select(string? id)
    {
        if (IsDisposed || (id == SelectedId && Editor is not null))
            return;
        SelectedId = id;
        RefreshRows();
        BuildEditor();
    }

    [RelayCommand]
    private void Close() => _panel.Close();

    [RelayCommand]
    private async Task AddAsync()
    {
        try
        {
            var profile = await _profiles.AddAsync();
            Select(profile.Id);
        }
        catch (Exception ex)
        {
            Report(ex, "Couldn't add the AI integration");
        }
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteSelectedAsync()
    {
        try
        {
            var profile = Find(SelectedId);
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
        catch (Exception ex)
        {
            Report(ex, "Couldn't delete the AI integration");
        }
    }

    /// <summary>Makes a profile the active one, or turns AI off when it already is. Switching from
    /// another active profile asks first.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleActiveAsync(string id)
    {
        try
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
        catch (Exception ex)
        {
            Report(ex, "Couldn't change the active AI integration");
        }
    }

    private void Report(Exception ex, string what)
    {
        AppLog.Write(AppLogLevel.Warn, "ai", what, ex);
        _notifications?.Publish(NotificationSeverity.Error, ex.NotificationMessage(what), ex.NotificationDetails());
    }

    private AiServiceProfile? Find(string? id) => id is null ? null : _profiles.Profiles.FirstOrDefault(p => p.Id == id);

    private void RefreshRows()
    {
        var active = _profiles.ActiveProfile?.Id;
        Rows = [.. _profiles.Profiles.Select(p => new AiProfileRowViewModel(
            p.Id, _profiles.DisplayLabel(p), p.Id == active, p.Id == SelectedId, Select, ToggleActiveRow))];
        CanDelete = Find(SelectedId) is not null;
        Editor?.RefreshActive();
    }

    private Task ToggleActiveRow(string id) => ToggleActiveAsync(id);

    private void BuildEditor()
    {
        Editor?.Dispose();
        Editor = null;

        var profile = Find(SelectedId);
        if (profile is null)
            return;

        Editor = new AiProfileEditorViewModel(
            profile, _profiles, _catalog, _dialogs, _notifications, RefreshRows, ToggleActiveRow);
    }

    // The services raise on whichever thread changed them; the work hops to the UI thread
    // because it touches bound state.
    private void OnProfilesChanged(object? sender, EventArgs e) => _ui.Post(ApplyProfilesChanged);

    private void OnPanelChanged(object? sender, EventArgs e) => _ui.Post(ApplyPanelChanged);

    private void OnCatalogChanged(object? sender, string id) => _ui.Post(() => ApplyCatalogChanged(id));

    private void ApplyProfilesChanged()
    {
        if (IsDisposed)
            return;

        if (Find(SelectedId) is null)
        {
            SelectedId = _profiles.Profiles.FirstOrDefault()?.Id;
            RefreshRows();
            BuildEditor();
            return;
        }

        RefreshRows();
        if (Editor is { } editor && Find(editor.Id) is { } current)
            editor.Sync(current);
    }

    private void ApplyPanelChanged()
    {
        if (IsDisposed || !_panel.IsOpen)
            return;
        if (_panel.FocusProfileId is { } id && Find(id) is not null)
            Select(id);
    }

    private void ApplyCatalogChanged(string id)
    {
        if (IsDisposed || Editor is not { } editor || editor.Id != id)
            return;
        editor.OnCatalogChanged();
    }

    protected override void OnDisposed()
    {
        Editor?.Dispose();
    }
}
