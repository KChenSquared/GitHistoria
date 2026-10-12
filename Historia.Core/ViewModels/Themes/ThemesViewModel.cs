namespace Historia.ViewModels;

/// <summary>
/// The manage-themes panel. It lists the user's custom themes (one file each under
/// <c>themes/</c>) and edits one in place: name, Dark/Light base, launch splash, every
/// <see cref="HistoriaPalette"/> token and the nine lane colours. A hex that fails to parse is
/// flagged on its row and blocks <see cref="SaveDraftCommand"/>. Saving is the apply: when the
/// edited theme is the selected one, the theme service re-resolves and the chrome repaints live.
/// </summary>
public sealed partial class ThemesViewModel : ViewModelBase
{
    private static readonly string[] BaseNames = ["Dark", "Light"];

    private readonly ICustomThemeService _themes;
    private readonly IThemeService _theme;
    private readonly ISettingsService _cfg;
    private readonly IDialogService _dialogs;
    private readonly INavigationBus _nav;
    private readonly INotificationService? _notifications;

    // The theme being edited, or null while the list shows.
    private CustomTheme? _editing;
    private bool _loadingDraft;

    public ThemesViewModel(
        ICustomThemeService themes,
        IThemeService theme,
        ISettingsService settings,
        IDialogService dialogs,
        INavigationBus nav,
        INotificationService? notifications = null)
    {
        _themes = themes;
        _theme = theme;
        _cfg = settings;
        _dialogs = dialogs;
        _nav = nav;
        _notifications = notifications;
        Themes = [];
        TokenRows = [];
        LaneRows = [];
        RefreshList();
    }

    /// <summary>The saved themes, by name. Replaced wholesale on every refresh.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListEmpty))]
    public partial IReadOnlyList<ThemeRowViewModel> Themes { get; private set; }

    public bool IsListEmpty => Themes.Count == 0;

    /// <summary>True while a theme is open in the editor, false while the list shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListing))]
    public partial bool IsEditing { get; private set; }

    public bool IsListing => !IsEditing;

    /// <summary>The choices for <see cref="DraftBase"/>.</summary>
    public IReadOnlyList<string> BaseChoices => BaseNames;

    [ObservableProperty]
    public partial string? DraftName { get; set; }

    /// <summary>The draft's base theme: <c>Dark</c> or <c>Light</c>. Changing it moves every row's
    /// watermark and inheriting swatch to the new base.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SplashWatermark))]
    public partial string DraftBase { get; set; } = "Dark";

    /// <summary>The draft's splash image path or <c>avares://</c> location; empty uses the built-in.</summary>
    [ObservableProperty]
    public partial string? DraftSplashPath { get; set; }

    /// <summary>How long the splash shows, whole seconds; the box clamps it to 0 to 10.</summary>
    [ObservableProperty]
    public partial decimal? DraftSplashSeconds { get; set; } = 2m;

    /// <summary>The built-in splash for the draft's base, shown while the path box is empty.</summary>
    public string SplashWatermark => CustomTheme.BuiltInSplashLocation(DraftBase);

    [ObservableProperty]
    public partial IReadOnlyList<ColorRowViewModel> TokenRows { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ColorRowViewModel> LaneRows { get; private set; }

    /// <summary>How many colour boxes hold an unparseable value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrors))]
    [NotifyPropertyChangedFor(nameof(ErrorSummary))]
    public partial int ErrorCount { get; private set; }

    public bool HasErrors => ErrorCount > 0;

    public string ErrorSummary => $"{ErrorCount} invalid colour(s) — fix before saving.";

    private EffectiveTheme DraftBaseTheme =>
        string.Equals(DraftBase, "Light", StringComparison.OrdinalIgnoreCase)
            ? EffectiveTheme.Light : EffectiveTheme.Dark;

    /// <summary>Parses <c>#RRGGBB</c> or <c>#RRGGBBAA</c>; false for anything else.</summary>
    public static bool TryParseHex(string? s, out Rgb color)
    {
        color = default;
        if (s is null)
            return false;

        try
        {
            color = Rgb.Parse(s);
            return true;
        }
        catch
        {
            return false;
        }
    }

    partial void OnDraftBaseChanged(string value)
    {
        if (_loadingDraft)
            return;

        // The watermarks and inheriting swatches follow the new base.
        var theme = DraftBaseTheme;
        foreach (var row in TokenRows)
            row.SetBase(HistoriaPalette.Get(theme, row.Label).ToHex());
        for (var i = 0; i < LaneRows.Count; i++)
            LaneRows[i].SetBase(LanePalette.ColorFor(i, theme).ToHex());
    }

    // ── list ────────────────────────────────────────────────────────────────────────────
    private void RefreshList()
    {
        if (IsDisposed)
            return;

        var selectedId = _cfg.Current.CustomThemeId;
        var customActive = _theme.CustomThemeActive;
        Themes = [.. _themes.List().Select(t => new ThemeRowViewModel(
            t,
            customActive && selectedId == t.Id,
            SwatchFor(t),
            EditCommand,
            DuplicateCommand,
            DeleteCommand))];
    }

    private static string SwatchFor(CustomTheme theme)
    {
        var hex = theme.Tokens.GetValueOrDefault("bg");
        return TryParseHex(hex, out _) ? hex! : HistoriaPalette.Get(theme.BaseTheme, "bg").ToHex();
    }

    [RelayCommand]
    private async Task NewAsync(string? baseName)
    {
        try
        {
            var theme = _themes.NewFrom(baseName ?? "Dark");
            LoadDraft(theme);
            IsEditing = true;
            await SaveThemeAsync(theme);
        }
        catch (Exception ex)
        {
            Report(ex, "Creating the theme");
        }
    }

    [RelayCommand]
    private void Edit(ThemeRowViewModel? row)
    {
        if (row is null)
            return;

        LoadDraft(row.Theme);
        IsEditing = true;
    }

    [RelayCommand]
    private async Task DuplicateAsync(ThemeRowViewModel? row)
    {
        if (row is null)
            return;

        try
        {
            var t = row.Theme;
            var copy = _themes.NewFrom(t.Base) with
            {
                Name = t.Name + " copy",
                Tokens = new Dictionary<string, string>(t.Tokens, StringComparer.Ordinal),
                Lane = t.Lane.ToArray(),
                SplashImagePath = t.SplashImagePath,
                SplashSeconds = t.SplashSeconds,
            };
            await SaveThemeAsync(copy);
            RefreshList();
        }
        catch (Exception ex)
        {
            Report(ex, "Duplicating the theme");
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(ThemeRowViewModel? row)
    {
        if (row is null)
            return;

        try
        {
            var t = row.Theme;
            if (!await _dialogs.ConfirmDestructiveAsync("Delete theme", $"Delete “{t.Name}”? This cannot be undone.", "Delete"))
                return;

            var wasSelected = _cfg.Current.CustomThemeId == t.Id;
            await _themes.DeleteAsync(t.Id);
            if (wasSelected)
                await _theme.SetCustomThemeEnabledAsync(false);
            RefreshList();
        }
        catch (Exception ex)
        {
            Report(ex, "Deleting the theme");
        }
    }

    /// <summary>Saves <paramref name="theme"/>, then selects it in the theme service when it is
    /// the selected theme (so its id and base re-resolve) or when the custom-theme override is on
    /// with no saved theme selected (so the first theme renders as soon as it exists).</summary>
    private async Task SaveThemeAsync(CustomTheme theme)
    {
        await _themes.SaveAsync(theme);
        var settings = _cfg.Current;
        var stored = settings.CustomThemeId;
        var noneSelected = string.IsNullOrEmpty(stored) || _themes.Get(stored) is null;
        if (stored == theme.Id || (settings.UseCustomTheme && noneSelected))
            await _theme.SelectCustomThemeAsync(theme.Id);
    }

    [RelayCommand]
    private void Close() => _nav.RequestNavigate("dev");

    // ── editor ──────────────────────────────────────────────────────────────────────────
    private void LoadDraft(CustomTheme t)
    {
        _editing = t;
        _loadingDraft = true;
        try
        {
            DraftName = t.Name;
            DraftBase = t.BaseTheme == EffectiveTheme.Light ? "Light" : "Dark";
            DraftSplashPath = t.SplashImagePath ?? "";
            DraftSplashSeconds = t.SplashSeconds;
        }
        finally
        {
            _loadingDraft = false;
        }

        var theme = DraftBaseTheme;
        TokenRows = [.. HistoriaPalette.Tokens.Select(token => new ColorRowViewModel(
            token,
            HistoriaPalette.Get(theme, token).ToHex(),
            t.Tokens.GetValueOrDefault(token),
            RecountErrors))];
        LaneRows = [.. Enumerable.Range(0, LanePalette.Size).Select(i => new ColorRowViewModel(
            $"lane {i}",
            LanePalette.ColorFor(i, theme).ToHex(),
            i < t.Lane.Count ? t.Lane[i] : null,
            RecountErrors))];
        RecountErrors();
    }

    private void RecountErrors()
        => ErrorCount = TokenRows.Count(r => r.IsInvalid) + LaneRows.Count(r => r.IsInvalid);

    [RelayCommand]
    private void Back()
    {
        _editing = null;
        IsEditing = false;
        RefreshList();
    }

    [RelayCommand]
    private async Task BrowseSplashAsync()
    {
        try
        {
            var picked = await _dialogs.PickFileAsync("Splash image", "PNG image|*.png|All files|*");
            if (picked is not null)
                DraftSplashPath = picked;
        }
        catch (Exception ex)
        {
            Report(ex, "Choosing the splash image");
        }
    }

    [RelayCommand]
    private async Task SaveDraftAsync()
    {
        if (_editing is null || HasErrors)
            return;

        try
        {
            var name = string.IsNullOrWhiteSpace(DraftName) ? _editing.Name : DraftName.Trim();
            var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in TokenRows)
            {
                if (row.Value is { } value)
                    tokens[row.Label] = value;
            }

            var lanes = new List<string?>(LaneRows.Select(r => r.Value));
            while (lanes.Count > 0 && lanes[^1] is null)
                lanes.RemoveAt(lanes.Count - 1);

            var updated = _editing with
            {
                Name = name,
                Base = DraftBase,
                Tokens = tokens,
                Lane = lanes,
                SplashImagePath = string.IsNullOrWhiteSpace(DraftSplashPath) ? null : DraftSplashPath.Trim(),
                SplashSeconds = Math.Clamp((int)(DraftSplashSeconds ?? 0m), 0, 10),
            };
            await SaveThemeAsync(updated);

            _editing = null;
            IsEditing = false;
            RefreshList();
        }
        catch (Exception ex)
        {
            Report(ex, "Saving the theme");
        }
    }

    private void Report(Exception ex, string what)
    {
        AppLog.Write(AppLogLevel.Warn, "themes", $"{what} failed.", ex);
        _notifications?.Publish(NotificationSeverity.Error, ex.NotificationMessage(what), ex.NotificationDetails());
    }
}
