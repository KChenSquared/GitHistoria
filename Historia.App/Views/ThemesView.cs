namespace Historia.Views;

/// <summary>
/// the manage-themes panel, opened from and closing to the Developer panel. Lists the user's
/// custom themes (one file each under <c>themes/</c>), and edits one in place: name, Dark/Light
/// base, every <see cref="HistoriaPalette"/> token, and the nine lane colours. A hex that fails to
/// parse shows inline and blocks Save. Saving is the apply — if the edited theme is active the
/// graph and chrome repaint live through <see cref="IThemeService.ThemeChanged"/>.
/// </summary>
public sealed class ThemesView : UserControl, IDisposable
{
    private readonly ICustomThemeService _themes;
    private readonly IThemeService _theme;
    private readonly ISettingsService _cfg;
    private readonly IDialogService _dialogs;
    private readonly StackPanel _root = new() { Spacing = 16, Margin = new Thickness(32), MaxWidth = 720 };

    // Editor draft state — null when showing the list.
    private CustomTheme? _editing;
    private string _draftName = "";
    private string _draftBase = "Dark";
    private readonly Dictionary<string, string> _draftTokens = new(StringComparer.Ordinal);
    private readonly string?[] _draftLanes = new string?[LanePalette.Size];
    private string? _draftSplashPath;
    private int _draftSplashSeconds = 2;
    private readonly HashSet<string> _errors = [];

    public ThemesView(IServiceProvider sp)
    {
        _themes = sp.GetRequiredService<ICustomThemeService>();
        _theme = sp.GetRequiredService<IThemeService>();
        _cfg = sp.GetRequiredService<ISettingsService>();
        _dialogs = sp.GetRequiredService<IDialogService>();

        Content = ClosablePane(new ScrollViewer { Content = _root }, sp, closeTo: "dev");
        this.Dyn(BackgroundProperty, "ColorChrome");
        Rebuild();
    }

    private void Rebuild()
    {
        _root.Children.Clear();
        if (_editing is null)
            BuildList();
        else
            BuildEditor();
    }

    // ── list ────────────────────────────────────────────────────────────────────────────
    private void BuildList()
    {
        _root.Children.Add(Text("Themes", "ColorText", 24, FontWeight.SemiBold));
        _root.Children.Add(WrapText(
            "Custom themes override any subset of the palette and the nine graph-lane colours " +
            "over a Dark or Light base. Each theme is saved to its own file under the " +
            "themes folder. Saving applies it."));

        var newRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        newRow.Children.Add(Text("New theme:", "ColorTextMuted", 13));
        newRow.Children.Add(TextButton("from Dark", () => StartNew("Dark")));
        newRow.Children.Add(TextButton("from Light", () => StartNew("Light")));
        _root.Children.Add(newRow);

        var list = _themes.List();
        if (list.Count == 0)
        {
            _root.Children.Add(Text("No custom themes yet.", "ColorTextMuted", 12));
            return;
        }

        foreach (var t in list)
        {
            var theme = t;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var active = _theme.CustomThemeActive && _cfg.Current.CustomThemeId == theme.Id;
            row.Children.Add(new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(4),
                Background = SafeBrush(theme.Tokens.GetValueOrDefault("bg"), theme.BaseTheme, "bg"),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x88, 0x88, 0x88)),
            });
            row.Children.Add(Text(theme.Name + (active ? "  (active)" : ""), "ColorText", 13));
            row.Children.Add(Text("base: " + theme.Base, "ColorTextMuted", 11));
            row.Children.Add(TextButton("Edit", () => StartEdit(theme)));
            row.Children.Add(TextButton("Duplicate", () => Duplicate(theme)));
            row.Children.Add(TextButton("Delete", () => _ = DeleteAsync(theme)));
            _root.Children.Add(row);
        }
    }

    private void StartNew(string baseName)
    {
        var t = _themes.NewFrom(baseName);
        LoadDraft(t);
        _ = SaveThemeAsync(t);
        Rebuild();
    }

    private void StartEdit(CustomTheme t)
    {
        LoadDraft(t);
        Rebuild();
    }

    private void Duplicate(CustomTheme t)
    {
        var copy = _themes.NewFrom(t.Base) with
        {
            Name = t.Name + " copy",
            Tokens = new Dictionary<string, string>(t.Tokens, StringComparer.Ordinal),
            Lane = t.Lane.ToArray(),
            SplashImagePath = t.SplashImagePath,
            SplashSeconds = t.SplashSeconds,
        };
        _ = SaveThemeAsync(copy);
        Rebuild();
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

    private async Task DeleteAsync(CustomTheme t)
    {
        if (!await _dialogs.ConfirmDestructiveAsync("Delete theme", $"Delete “{t.Name}”? This cannot be undone.", "Delete"))
            return;

        var wasSelected = _cfg.Current.CustomThemeId == t.Id;
        await _themes.DeleteAsync(t.Id);
        if (wasSelected)
            await _theme.SetCustomThemeEnabledAsync(false);
        Rebuild();
    }

    // ── editor ──────────────────────────────────────────────────────────────────────────
    private void LoadDraft(CustomTheme t)
    {
        _editing = t;
        _draftName = t.Name;
        _draftBase = t.Base;
        _draftTokens.Clear();
        foreach (var (k, v) in t.Tokens)
            _draftTokens[k] = v;
        for (var i = 0; i < _draftLanes.Length; i++)
            _draftLanes[i] = i < t.Lane.Count ? t.Lane[i] : null;
        _draftSplashPath = t.SplashImagePath;
        _draftSplashSeconds = t.SplashSeconds;
        _errors.Clear();
    }

    private EffectiveTheme DraftBaseTheme =>
        string.Equals(_draftBase, "Light", StringComparison.OrdinalIgnoreCase)
            ? EffectiveTheme.Light : EffectiveTheme.Dark;

    private void BuildEditor()
    {
        var t = _editing!;
        _root.Children.Add(TextButton("← Back to themes", () => { _editing = null; Rebuild(); }));
        _root.Children.Add(Text("Edit theme", "ColorText", 22, FontWeight.SemiBold));

        var nameBox = new TextBox { Text = _draftName, Width = 260, Watermark = "Theme name" };
        nameBox.TextChanged += (_, _) => _draftName = nameBox.Text ?? "";
        _root.Children.Add(LabeledRow("Name", nameBox));

        var baseCombo = new ComboBox { Width = 140, ItemsSource = new[] { "Dark", "Light" } };
        baseCombo.SelectedIndex = DraftBaseTheme == EffectiveTheme.Light ? 1 : 0;
        baseCombo.SelectionChanged += (_, _) =>
        {
            _draftBase = baseCombo.SelectedIndex == 1 ? "Light" : "Dark";
            Rebuild(); // watermarks + swatches follow the new base
        };
        _root.Children.Add(LabeledRow("Base", baseCombo));

        _root.Children.Add(Section("Launch splash"));
        var splashBox = new TextBox
        {
            Text = _draftSplashPath ?? "",
            Width = 340,
            Watermark = CustomTheme.BuiltInSplashLocation(_draftBase),
        };
        splashBox.TextChanged += (_, _) =>
            _draftSplashPath = string.IsNullOrWhiteSpace(splashBox.Text) ? null : splashBox.Text!.Trim();
        var browse = TextButton("Browse…", async () =>
        {
            var picked = await _dialogs.PickFileAsync("Splash image", "PNG image|*.png|All files|*");
            if (picked is not null)
                splashBox.Text = picked; // fires TextChanged -> _draftSplashPath
        });
        var splashRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        splashRow.Children.Add(splashBox);
        splashRow.Children.Add(browse);
        _root.Children.Add(LabeledRow("Image", splashRow));
        _root.Children.Add(WrapText(
            "A complete PNG — background, emblem and wordmark baked in. Empty uses the built-in " +
            "splash for the base theme."));

        var secondsBox = new NumericUpDown
        {
            Minimum = 0, Maximum = 10, Increment = 1, FormatString = "0", Width = 100,
            Value = (decimal)_draftSplashSeconds,
        };
        secondsBox.ValueChanged += (_, e) => _draftSplashSeconds = (int)(e.NewValue ?? 0m);
        _root.Children.Add(LabeledRow("Seconds", secondsBox));
        _root.Children.Add(WrapText("How long the splash shows, 0–10. 0 disables the splash for this theme."));

        _root.Children.Add(Section("Palette tokens"));
        foreach (var token in HistoriaPalette.Tokens)
            _root.Children.Add(ColorRow(token, HistoriaPalette.Get(DraftBaseTheme, token).ToHex(),
                _draftTokens.GetValueOrDefault(token),
                val => { if (val is null) _draftTokens.Remove(token); else _draftTokens[token] = val; },
                "tok:" + token));

        _root.Children.Add(Section("Graph lane colours"));
        for (var i = 0; i < _draftLanes.Length; i++)
        {
            var idx = i;
            _root.Children.Add(ColorRow($"lane {idx}", LanePalette.ColorFor(idx, DraftBaseTheme).ToHex(),
                _draftLanes[idx],
                val => _draftLanes[idx] = val,
                "lane:" + idx));
        }

        var saveRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        saveRow.Children.Add(TextButton("Save", () => _ = SaveDraftAsync(), primary: true));
        saveRow.Children.Add(TextButton("Cancel", () => { _editing = null; Rebuild(); }));
        if (_errors.Count > 0)
            saveRow.Children.Add(Text($"{_errors.Count} invalid colour(s) — fix before saving.", "ColorDanger", 12));
        _root.Children.Add(saveRow);
    }

    /// <summary>A label + hex box + live swatch. An unparseable non-empty value records an error
    /// (keyed by <paramref name="errKey"/>) and shows inline; empty means "inherit the base".</summary>
    private Control ColorRow(string label, string baseHex, string? current, Action<string?> apply, string errKey)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new TextBlock
        {
            Text = label, Width = 120, VerticalAlignment = VerticalAlignment.Center,
        }.Dyn(TextBlock.ForegroundProperty, "ColorTextMuted"));

        var swatch = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x88, 0x88, 0x88)),
            Background = HexBrush(current) ?? HexBrush(baseHex),
        };
        var box = new TextBox { Text = current ?? "", Width = 130, Watermark = baseHex };
        var err = new TextBlock { Text = "", VerticalAlignment = VerticalAlignment.Center }
            .Dyn(TextBlock.ForegroundProperty, "ColorDanger");

        box.TextChanged += (_, _) =>
        {
            var v = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text!.Trim();
            if (v is null)
            {
                _errors.Remove(errKey);
                err.Text = "";
                swatch.Background = HexBrush(baseHex);
                apply(null);
            }
            else if (TryHex(v, out var color))
            {
                _errors.Remove(errKey);
                err.Text = "";
                swatch.Background = new SolidColorBrush(color);
                apply(v);
            }
            else
            {
                _errors.Add(errKey);
                err.Text = "invalid";
                apply(null); // don't persist a bad value
            }
        };

        row.Children.Add(swatch);
        row.Children.Add(box);
        row.Children.Add(err);
        return row;
    }

    private async Task SaveDraftAsync()
    {
        if (_errors.Count > 0) { Rebuild(); return; }

        var name = string.IsNullOrWhiteSpace(_draftName) ? _editing!.Name : _draftName.Trim();
        var lanes = new List<string?>(_draftLanes);
        while (lanes.Count > 0 && lanes[^1] is null)
            lanes.RemoveAt(lanes.Count - 1);

        var updated = _editing! with
        {
            Name = name,
            Base = _draftBase,
            Tokens = new Dictionary<string, string>(_draftTokens, StringComparer.Ordinal),
            Lane = lanes,
            SplashImagePath = string.IsNullOrWhiteSpace(_draftSplashPath) ? null : _draftSplashPath!.Trim(),
            SplashSeconds = Math.Clamp(_draftSplashSeconds, 0, 10),
        };
        await SaveThemeAsync(updated);

        _editing = null;
        Rebuild();
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────
    private static TextBlock WrapText(string s)
    {
        var t = Text(s, "ColorTextMuted", 12);
        t.TextWrapping = TextWrapping.Wrap;
        return t;
    }

    private static Control LabeledRow(string label, Control control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new TextBlock { Text = label, Width = 120, VerticalAlignment = VerticalAlignment.Center }
            .Dyn(TextBlock.ForegroundProperty, "ColorTextMuted"));
        row.Children.Add(control);
        return row;
    }

    private static bool TryHex(string s, out Color color)
    {
        try
        {
            color = Rgb.Parse(s).ToAvalonia();
            return true;
        }
        catch
        {
            color = default;
            return false;
        }
    }

    private static SolidColorBrush? HexBrush(string? s) =>
        s is not null && TryHex(s, out var c) ? new SolidColorBrush(c) : null;

    private static IBrush SafeBrush(string? hex, EffectiveTheme baseTheme, string token)
    {
        if (hex is not null && TryHex(hex, out var c))
            return new SolidColorBrush(c);
        return new SolidColorBrush(HistoriaPalette.Get(baseTheme, token).ToAvalonia());
    }

    public void Dispose() { }
}
