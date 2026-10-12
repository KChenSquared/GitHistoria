namespace Historia.Tests;

/// <summary>
/// The Themes panel's view model: hex validation, the draft-to-saved-theme round trip, the
/// selection rule on save, and the destructive confirm on delete.
/// </summary>
[Trait("tier", "unit")]
public class ThemesViewModelTests
{
    private static readonly CustomTheme Alpha = new() { Id = "a", Name = "Alpha", Base = "Dark" };

    private sealed class Rig : IDisposable
    {
        public Rig(AppSettings? settings = null, ICustomThemeService? customOverride = null, params CustomTheme[] themes)
        {
            Settings = new FakeSettingsService(settings);
            Custom = customOverride ?? new MemoryThemes(themes);
            Theme = new ThemeService(Settings, new DarkProbe(), Custom);
            Dialogs = new FakeDialogService();
            Nav = new NavigationBus();
            Notifications = new FakeNotificationService();
            Vm = new ThemesViewModel(Custom, Theme, Settings, Dialogs, Nav, Notifications);
        }

        public FakeSettingsService Settings { get; }
        public ICustomThemeService Custom { get; }
        public ThemeService Theme { get; }
        public FakeDialogService Dialogs { get; }
        public NavigationBus Nav { get; }
        public FakeNotificationService Notifications { get; }
        public ThemesViewModel Vm { get; }

        public ThemeRowViewModel Row(string id) => Vm.Themes.Single(r => r.Theme.Id == id);

        public void Dispose() => Vm.Dispose();
    }

    private sealed class MemoryThemes(params CustomTheme[] themes) : ICustomThemeService
    {
        private readonly List<CustomTheme> _themes = [.. themes];
        private int _created;

        public event EventHandler? Changed;

        public Exception? FailSave { get; set; }

        public IReadOnlyList<CustomTheme> List() => _themes.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

        public CustomTheme? Get(string id) => _themes.FirstOrDefault(t => t.Id == id);

        public Task<CustomTheme> SaveAsync(CustomTheme theme, CancellationToken ct = default)
        {
            if (FailSave is not null)
                throw FailSave;
            _themes.RemoveAll(t => t.Id == theme.Id);
            _themes.Add(theme);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(theme);
        }

        public Task DeleteAsync(string id, CancellationToken ct = default)
        {
            _themes.RemoveAll(t => t.Id == id);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public CustomTheme NewFrom(string baseName) =>
            new() { Id = $"new-{++_created}", Name = $"Custom {_themes.Count + 1}", Base = baseName };

        public ResolvedPalette Compose(CustomTheme theme) => new CustomThemeService().Compose(theme);
    }

    private sealed class DarkProbe : ISystemThemeProbe
    {
        public bool IsLightTheme => false;
        public bool IsHighContrast => false;
#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067
    }

    // ── hex validation ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("#112233", true)]
    [InlineData("112233", true)]
    [InlineData("#11223344", true)]
    [InlineData("#abcdef", true)]
    [InlineData("#12345", false)]
    [InlineData("#abc", false)]
    [InlineData("#gg0000", false)]
    [InlineData("red", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryParseHex_accepts_only_six_or_eight_digit_hex(string? text, bool valid)
    {
        Assert.Equal(valid, ThemesViewModel.TryParseHex(text, out _));
    }

    [Fact]
    public void TryParseHex_reads_the_channels()
    {
        Assert.True(ThemesViewModel.TryParseHex("#102030", out var c));
        Assert.Equal(Rgb.FromRgb(0x10, 0x20, 0x30), c);
    }

    [Fact]
    public void A_colour_row_inherits_when_empty_overrides_when_valid_and_flags_anything_else()
    {
        var changes = 0;
        var row = new ColorRowViewModel("bg", "#101010", null, () => changes++);
        Assert.Null(row.Value);
        Assert.False(row.IsInvalid);
        Assert.Equal("#101010", row.SwatchHex);

        row.Text = "  #ff0000 ";
        Assert.Equal("#ff0000", row.Value);
        Assert.Equal("#ff0000", row.SwatchHex);
        Assert.False(row.IsInvalid);
        Assert.Equal("", row.ErrorText);

        row.Text = "nope";
        Assert.Null(row.Value);
        Assert.True(row.IsInvalid);
        Assert.Equal("invalid", row.ErrorText);
        Assert.Equal("#ff0000", row.SwatchHex); // an invalid entry leaves the swatch alone

        row.Text = "   ";
        Assert.Null(row.Value);
        Assert.False(row.IsInvalid);
        Assert.Equal("#101010", row.SwatchHex);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void An_invalid_value_loaded_from_a_theme_is_flagged_and_blocks_saving()
    {
        using var rig = new Rig(null, null, new CustomTheme
        {
            Id = "bad",
            Name = "Bad",
            Tokens = new Dictionary<string, string> { ["bg"] = "not-a-colour" },
        });

        rig.Vm.EditCommand.Execute(rig.Row("bad"));

        Assert.Equal(1, rig.Vm.ErrorCount);
        Assert.True(rig.Vm.HasErrors);
        Assert.Equal("1 invalid colour(s) — fix before saving.", rig.Vm.ErrorSummary);
    }

    // ── list ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_list_shows_saved_themes_and_marks_the_active_one()
    {
        var beta = new CustomTheme { Id = "b", Name = "Beta", Base = "Light" };
        using var rig = new Rig(new AppSettings { UseCustomTheme = true, CustomThemeId = "a" }, null, beta, Alpha);

        Assert.False(rig.Vm.IsListEmpty);
        Assert.Equal(["Alpha", "Beta"], rig.Vm.Themes.Select(r => r.Name));
        Assert.Equal("Alpha  (active)", rig.Row("a").DisplayName);
        Assert.Equal("Beta", rig.Row("b").DisplayName);
        Assert.Equal("base: Light", rig.Row("b").BaseLabel);
        Assert.True(rig.Vm.IsListing);
        Assert.False(rig.Vm.IsEditing);
    }

    [Fact]
    public void The_swatch_is_the_themes_background_or_its_bases()
    {
        var custom = new CustomTheme
        {
            Id = "c",
            Name = "Custom",
            Base = "Light",
            Tokens = new Dictionary<string, string> { ["bg"] = "#123456" },
        };
        using var rig = new Rig(null, null, custom, Alpha);

        Assert.Equal("#123456", rig.Row("c").SwatchHex);
        Assert.Equal(HistoriaPalette.Get(EffectiveTheme.Dark, "bg").ToHex(), rig.Row("a").SwatchHex);
    }

    [Fact]
    public void An_empty_store_reports_an_empty_list()
    {
        using var rig = new Rig();

        Assert.True(rig.Vm.IsListEmpty);
        Assert.Empty(rig.Vm.Themes);
    }

    [Fact]
    public async Task Duplicate_saves_a_copy_with_the_same_colours_and_refreshes_the_list()
    {
        var source = new CustomTheme
        {
            Id = "a",
            Name = "Alpha",
            Base = "Light",
            Tokens = new Dictionary<string, string> { ["bg"] = "#123456" },
            Lane = ["#ff0000", null],
            SplashSeconds = 5,
        };
        using var rig = new Rig(null, null, source);

        await rig.Vm.DuplicateCommand.ExecuteAsync(rig.Row("a"));

        Assert.Equal(2, rig.Vm.Themes.Count);
        var copy = rig.Vm.Themes.Single(r => r.Theme.Id != "a").Theme;
        Assert.Equal("Alpha copy", copy.Name);
        Assert.Equal("#123456", copy.Tokens["bg"]);
        Assert.Equal(["#ff0000", null], copy.Lane);
        Assert.Equal(5, copy.SplashSeconds);
        Assert.Equal("Light", copy.Base);
    }

    [Fact]
    public async Task New_opens_the_editor_on_a_saved_theme_and_selects_it_when_the_override_is_on_with_none_selected()
    {
        using var rig = new Rig(new AppSettings { UseCustomTheme = true });

        await rig.Vm.NewCommand.ExecuteAsync("Light");

        Assert.True(rig.Vm.IsEditing);
        var created = Assert.Single(rig.Custom.List());
        Assert.Equal("Light", created.Base);
        Assert.Equal("Light", rig.Vm.DraftBase);
        Assert.Equal(created.Id, rig.Settings.Current.CustomThemeId);
        Assert.True(rig.Theme.CustomThemeActive);
    }

    [Fact]
    public async Task Saving_leaves_the_selection_alone_when_the_override_is_off()
    {
        using var rig = new Rig();

        await rig.Vm.NewCommand.ExecuteAsync("Dark");

        Assert.Single(rig.Custom.List());
        Assert.Null(rig.Settings.Current.CustomThemeId);
        Assert.False(rig.Theme.CustomThemeActive);
    }

    // ── delete ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_asks_for_confirmation_and_keeps_the_theme_when_cancelled()
    {
        using var rig = new Rig(new AppSettings { UseCustomTheme = true, CustomThemeId = "a" }, null, Alpha);
        rig.Dialogs.EnqueueConfirmDestructive(false);

        await rig.Vm.DeleteCommand.ExecuteAsync(rig.Row("a"));

        var asked = Assert.Single(rig.Dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Delete theme", asked.Title);
        Assert.Contains("Alpha", asked.Effect);
        Assert.Equal("Delete", asked.ConfirmLabel);
        Assert.Single(rig.Custom.List());
        Assert.Single(rig.Vm.Themes);
        Assert.True(rig.Settings.Current.UseCustomTheme);
    }

    [Fact]
    public async Task Delete_removes_the_theme_and_turns_the_override_off_when_it_was_selected()
    {
        var beta = new CustomTheme { Id = "b", Name = "Beta" };
        using var rig = new Rig(new AppSettings { UseCustomTheme = true, CustomThemeId = "a" }, null, Alpha, beta);
        rig.Dialogs.EnqueueConfirmDestructive(true);

        await rig.Vm.DeleteCommand.ExecuteAsync(rig.Row("a"));

        Assert.Equal(["Beta"], rig.Vm.Themes.Select(r => r.Name));
        Assert.Null(rig.Custom.Get("a"));
        Assert.False(rig.Settings.Current.UseCustomTheme);
    }

    [Fact]
    public async Task Delete_of_an_unselected_theme_leaves_the_override_alone()
    {
        var beta = new CustomTheme { Id = "b", Name = "Beta" };
        using var rig = new Rig(new AppSettings { UseCustomTheme = true, CustomThemeId = "a" }, null, Alpha, beta);
        rig.Dialogs.EnqueueConfirmDestructive(true);

        await rig.Vm.DeleteCommand.ExecuteAsync(rig.Row("b"));

        Assert.Equal(["Alpha"], rig.Vm.Themes.Select(r => r.Name));
        Assert.True(rig.Settings.Current.UseCustomTheme);
    }

    // ── editor ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Edit_loads_the_draft_and_one_row_per_token_and_lane()
    {
        var theme = new CustomTheme
        {
            Id = "a",
            Name = "Alpha",
            Base = "Light",
            Tokens = new Dictionary<string, string> { ["bg"] = "#123456" },
            Lane = ["#ff0000"],
            SplashImagePath = "C:/splash.png",
            SplashSeconds = 4,
        };
        using var rig = new Rig(null, null, theme);

        rig.Vm.EditCommand.Execute(rig.Row("a"));

        Assert.True(rig.Vm.IsEditing);
        Assert.False(rig.Vm.IsListing);
        Assert.Equal("Alpha", rig.Vm.DraftName);
        Assert.Equal("Light", rig.Vm.DraftBase);
        Assert.Equal("C:/splash.png", rig.Vm.DraftSplashPath);
        Assert.Equal(4m, rig.Vm.DraftSplashSeconds);
        Assert.Equal(HistoriaPalette.Tokens, rig.Vm.TokenRows.Select(r => r.Label));
        Assert.Equal(LanePalette.Size, rig.Vm.LaneRows.Count);
        Assert.Equal("#123456", rig.Vm.TokenRows.Single(r => r.Label == "bg").Text);
        Assert.Equal("#ff0000", rig.Vm.LaneRows[0].Text);
        Assert.Equal("", rig.Vm.LaneRows[1].Text);
        Assert.Equal(0, rig.Vm.ErrorCount);
    }

    [Fact]
    public void Changing_the_base_moves_the_watermarks_and_inheriting_swatches()
    {
        var theme = new CustomTheme
        {
            Id = "a",
            Name = "Alpha",
            Base = "Dark",
            Tokens = new Dictionary<string, string> { ["bg"] = "#123456" },
        };
        using var rig = new Rig(null, null, theme);
        rig.Vm.EditCommand.Execute(rig.Row("a"));
        var bg = rig.Vm.TokenRows.Single(r => r.Label == "bg");
        var surface = rig.Vm.TokenRows.Single(r => r.Label == "surface");
        Assert.Equal(CustomTheme.BuiltInSplashLocation("Dark"), rig.Vm.SplashWatermark);

        rig.Vm.DraftBase = "Light";

        Assert.Equal(HistoriaPalette.Get(EffectiveTheme.Light, "bg").ToHex(), bg.BaseHex);
        Assert.Equal("#123456", bg.SwatchHex); // an override keeps its own colour
        Assert.Equal(HistoriaPalette.Get(EffectiveTheme.Light, "surface").ToHex(), surface.SwatchHex);
        Assert.Equal(LanePalette.ColorFor(3, EffectiveTheme.Light).ToHex(), rig.Vm.LaneRows[3].BaseHex);
        Assert.Equal(CustomTheme.BuiltInSplashLocation("Light"), rig.Vm.SplashWatermark);
    }

    [Fact]
    public void An_invalid_hex_counts_as_an_error_until_it_is_fixed()
    {
        using var rig = new Rig(null, null, Alpha);
        rig.Vm.EditCommand.Execute(rig.Row("a"));

        rig.Vm.TokenRows[0].Text = "zzz";
        rig.Vm.LaneRows[2].Text = "#12";
        Assert.Equal(2, rig.Vm.ErrorCount);
        Assert.True(rig.Vm.HasErrors);

        rig.Vm.TokenRows[0].Text = "#abcdef";
        Assert.Equal(1, rig.Vm.ErrorCount);
        rig.Vm.LaneRows[2].Text = "";
        Assert.Equal(0, rig.Vm.ErrorCount);
        Assert.False(rig.Vm.HasErrors);
    }

    [Fact]
    public async Task Save_does_nothing_while_a_colour_is_invalid()
    {
        using var rig = new Rig(null, null, Alpha);
        rig.Vm.EditCommand.Execute(rig.Row("a"));
        rig.Vm.DraftName = "Renamed";
        rig.Vm.TokenRows[0].Text = "zzz";

        await rig.Vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.True(rig.Vm.IsEditing);
        Assert.Equal("Alpha", rig.Custom.Get("a")!.Name);
    }

    [Fact]
    public async Task Save_writes_the_draft_and_returns_to_the_list()
    {
        using var rig = new Rig(null, null, Alpha);
        rig.Vm.EditCommand.Execute(rig.Row("a"));
        rig.Vm.DraftName = "  Renamed  ";
        rig.Vm.DraftBase = "Light";
        rig.Vm.DraftSplashPath = "  C:/mine.png ";
        rig.Vm.DraftSplashSeconds = 7m;
        rig.Vm.TokenRows.Single(r => r.Label == "bg").Text = "#112233";
        rig.Vm.TokenRows.Single(r => r.Label == "text").Text = "#445566";
        rig.Vm.LaneRows[1].Text = "#778899";

        await rig.Vm.SaveDraftCommand.ExecuteAsync(null);

        var saved = rig.Custom.Get("a")!;
        Assert.Equal("Renamed", saved.Name);
        Assert.Equal("Light", saved.Base);
        Assert.Equal("C:/mine.png", saved.SplashImagePath);
        Assert.Equal(7, saved.SplashSeconds);
        Assert.Equal(2, saved.Tokens.Count);
        Assert.Equal("#112233", saved.Tokens["bg"]);
        Assert.Equal("#445566", saved.Tokens["text"]);
        Assert.Equal([null, "#778899"], saved.Lane); // trailing blanks are trimmed
        Assert.False(rig.Vm.IsEditing);
        Assert.Equal("Renamed", Assert.Single(rig.Vm.Themes).Name);
    }

    [Fact]
    public async Task Save_round_trips_through_a_second_edit()
    {
        using var rig = new Rig(null, null, Alpha);
        rig.Vm.EditCommand.Execute(rig.Row("a"));
        rig.Vm.TokenRows.Single(r => r.Label == "accent").Text = "#0a0b0c";
        rig.Vm.LaneRows[4].Text = "#0d0e0f";
        await rig.Vm.SaveDraftCommand.ExecuteAsync(null);

        rig.Vm.EditCommand.Execute(rig.Row("a"));

        Assert.Equal("#0a0b0c", rig.Vm.TokenRows.Single(r => r.Label == "accent").Text);
        Assert.Equal("#0d0e0f", rig.Vm.LaneRows[4].Text);
        Assert.Equal("", rig.Vm.LaneRows[0].Text);
    }

    [Fact]
    public async Task Save_keeps_the_name_when_the_box_is_blank_and_clamps_the_seconds()
    {
        using var rig = new Rig(null, null, Alpha);
        rig.Vm.EditCommand.Execute(rig.Row("a"));
        rig.Vm.DraftName = "   ";
        rig.Vm.DraftSplashPath = "";
        rig.Vm.DraftSplashSeconds = 99m;

        await rig.Vm.SaveDraftCommand.ExecuteAsync(null);

        var saved = rig.Custom.Get("a")!;
        Assert.Equal("Alpha", saved.Name);
        Assert.Null(saved.SplashImagePath);
        Assert.Equal(10, saved.SplashSeconds);
    }

    [Fact]
    public async Task Saving_selects_the_theme_when_the_stored_id_is_stale()
    {
        using var rig = new Rig(new AppSettings { UseCustomTheme = true, CustomThemeId = "gone" }, null, Alpha);
        Assert.False(rig.Theme.CustomThemeActive);
        rig.Vm.EditCommand.Execute(rig.Row("a"));

        await rig.Vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.Equal("a", rig.Settings.Current.CustomThemeId);
        Assert.True(rig.Theme.CustomThemeActive);
    }

    [Fact]
    public async Task A_save_that_fails_reports_an_error_and_stays_in_the_editor()
    {
        var themes = new MemoryThemes(Alpha);
        using var rig = new Rig(null, themes);
        rig.Vm.EditCommand.Execute(rig.Row("a"));
        themes.FailSave = new IOException("disk full");

        await rig.Vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.True(rig.Vm.IsEditing);
        var notice = Assert.Single(rig.Notifications.Published);
        Assert.Equal(NotificationSeverity.Error, notice.Severity);
    }

    [Fact]
    public async Task Browse_fills_the_splash_path_and_a_cancelled_pick_keeps_it()
    {
        using var rig = new Rig(null, null, Alpha);
        rig.Vm.EditCommand.Execute(rig.Row("a"));
        rig.Dialogs.EnqueueFile("C:/picked.png").EnqueueFile(null);

        await rig.Vm.BrowseSplashCommand.ExecuteAsync(null);
        Assert.Equal("C:/picked.png", rig.Vm.DraftSplashPath);

        await rig.Vm.BrowseSplashCommand.ExecuteAsync(null);
        Assert.Equal("C:/picked.png", rig.Vm.DraftSplashPath);
    }

    [Fact]
    public void Back_and_close_leave_the_editor_and_the_panel()
    {
        using var rig = new Rig(null, null, Alpha);
        var routes = new List<string>();
        rig.Nav.NavigationRequested += routes.Add;
        rig.Vm.EditCommand.Execute(rig.Row("a"));

        rig.Vm.BackCommand.Execute(null);
        Assert.False(rig.Vm.IsEditing);
        Assert.True(rig.Vm.IsListing);

        rig.Vm.CloseCommand.Execute(null);
        Assert.Equal(["dev"], routes);
    }
}
