namespace Historia.Tests;
[Trait("tier", "unit")]

public class SettingsServiceTests : IDisposable
{
    private readonly StateDir _state = new();

    [Fact]
    public void Load_returns_defaults_when_no_file_exists()
    {
        var svc = SettingsService.Load();
        Assert.Equal(ThemeMode.System, svc.Current.AppTheme);
        Assert.Equal(18, svc.Current.SchemaVersion);
        Assert.False(svc.Current.ShowAuthorDate);
        Assert.False(svc.Current.EnableMultiLineTabs);
        Assert.Equal(3, svc.Current.MaxTabRows);
        Assert.True(svc.Current.PersistRepoCache);
        Assert.True(svc.Current.GraphMetadataFromDisk); // default on
        Assert.True(svc.Current.AutoFetchEnabled); // §12.1 default on
        Assert.False(svc.Current.EnableGraphSplice); // Default off
    }

    // ── §12.1: v15 → v16 (auto-fetch toggle) ──

    [Fact]
    public void V15_file_loads_with_auto_fetch_enabled_by_default()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 15, \"appTheme\": \"dark\" }");

        var svc = SettingsService.Load();

        Assert.Equal(18, svc.Current.SchemaVersion);
        Assert.True(svc.Current.AutoFetchEnabled);
    }

    [Fact]
    public async Task Auto_fetch_toggle_round_trips()
    {
        var svc = SettingsService.Load();
        await svc.UpdateAsync(s => s with { AutoFetchEnabled = false });
        await svc.FlushAsync();

        Assert.False(SettingsService.Load().Current.AutoFetchEnabled);
    }

    [Fact]
    public void V5_file_without_persist_repo_cache_migrates_to_the_default()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 5, \"appTheme\": \"dark\" }");
        var svc = SettingsService.Load();
        Assert.Equal(18, svc.Current.SchemaVersion);
        Assert.True(svc.Current.PersistRepoCache);
    }

    [Fact]
    public async Task UpdateAsync_mutates_persists_and_raises_changed()
    {
        var svc = SettingsService.Load();
        SettingsChangedEventArgs? seen = null;
        svc.Changed += (_, e) => seen = e;

        await svc.UpdateAsync(s => s with { AppTheme = ThemeMode.Light, TabWidth = 2 });
        await svc.FlushAsync();

        Assert.Equal(ThemeMode.Light, svc.Current.AppTheme);
        Assert.NotNull(seen);
        Assert.Equal(ThemeMode.Light, seen!.NewSettings.AppTheme);

        var reloaded = SettingsService.Load();
        Assert.Equal(ThemeMode.Light, reloaded.Current.AppTheme);
        Assert.Equal(2, reloaded.Current.TabWidth);
    }

    [Fact]
    public async Task Multi_line_tab_settings_round_trip()
    {
        var svc = SettingsService.Load();
        await svc.UpdateAsync(s => s with { EnableMultiLineTabs = true, MaxTabRows = 4 });
        await svc.FlushAsync();

        var reloaded = SettingsService.Load();
        Assert.True(reloaded.Current.EnableMultiLineTabs);
        Assert.Equal(4, reloaded.Current.MaxTabRows);
    }

    [Fact]
    public void Legacy_file_without_schema_version_is_migrated_to_current()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"appTheme\": \"light\" }");

        var svc = SettingsService.Load();

        Assert.Equal(18, svc.Current.SchemaVersion);
        Assert.Equal(ThemeMode.Light, svc.Current.AppTheme);
        var onDisk = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile))!;
        Assert.Equal(18, onDisk["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void V1_file_migrates_forward()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 1, \"appTheme\": \"dark\" }");

        var svc = SettingsService.Load();

        Assert.Equal(18, svc.Current.SchemaVersion);
        Assert.Equal(ThemeMode.Dark, svc.Current.AppTheme);
    }

    // ── §V3-13.3: v3 → v4 ──

    [Fact]
    public void V3_to_v4_turns_on_session_restore_and_bumps_recents_when_absent()
    {
        File.WriteAllText(AppPaths.SettingsFile, """
            {
              "schemaVersion": 3,
              "graphColumns": [
                { "kind": "refs", "width": 180, "visible": true },
                { "kind": "graph", "width": 220, "visible": true },
                { "kind": "message", "width": 400, "visible": true, "isFlex": true },
                { "kind": "author", "width": 150, "visible": true },
                { "kind": "date", "width": 110, "visible": true },
                { "kind": "sha", "width": 80, "visible": true }
              ]
            }
            """);

        var svc = SettingsService.Load();

        Assert.Equal(18, svc.Current.SchemaVersion);
        Assert.True(svc.Current.RestoreSessionTabs);
        Assert.Equal(30, svc.Current.MaxRecentRepositories);
    }

    [Fact]
    public void V3_to_v4_leaves_an_explicit_restore_session_false_alone()
    {
        File.WriteAllText(AppPaths.SettingsFile,
            "{ \"schemaVersion\": 3, \"restoreSessionTabs\": false, \"maxRecentRepositories\": 12 }");

        var svc = SettingsService.Load();

        Assert.False(svc.Current.RestoreSessionTabs);
        Assert.Equal(12, svc.Current.MaxRecentRepositories); // not 20, so left as-is
    }

    [Fact]
    public void Newer_schema_version_is_loaded_read_only_without_rewrite()
    {
        var future = "{ \"schemaVersion\": 999, \"appTheme\": \"light\" }";
        File.WriteAllText(AppPaths.SettingsFile, future);

        var svc = SettingsService.Load();

        Assert.Equal(ThemeMode.Light, svc.Current.AppTheme);
        Assert.Contains("999", File.ReadAllText(AppPaths.SettingsFile)); // untouched
    }

    // ── V5 §13: v8 → v9 (terminal) ──

    [Fact]
    public void V8_file_loads_with_terminal_defaults()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 8, \"appTheme\": \"dark\" }");

        var svc = SettingsService.Load();

        Assert.Equal(18, svc.Current.SchemaVersion);
        Assert.Null(svc.Current.TerminalShellPath);
        Assert.Empty(svc.Current.TerminalShellArgs);
        Assert.Equal(13, svc.Current.TerminalFontSize);
        Assert.Equal(5000, svc.Current.TerminalScrollbackLines);
        Assert.Equal(TerminalCursorStyle.Block, svc.Current.TerminalCursorStyle);
        Assert.True(svc.Current.TerminalCursorBlink);
        Assert.Equal(TerminalBellStyle.None, svc.Current.TerminalBellStyle);
        Assert.False(svc.Current.TerminalDockOpen);
        Assert.Equal(260, svc.Current.TerminalDockHeight);
    }

    [Fact]
    public async Task Schema_v9_terminal_settings_round_trip()
    {
        var svc = SettingsService.Load();
        await svc.UpdateAsync(s => s with
        {
            TerminalShellPath = "/usr/bin/fish",
            TerminalShellArgs = ["-i"],
            TerminalFontFamily = "Cascadia Mono",
            TerminalFontSize = 15,
            TerminalScrollbackLines = 12_000,
            TerminalDockHeight = 320,
            TerminalDockOpen = true,
            TerminalCursorStyle = TerminalCursorStyle.Bar,
            TerminalCursorBlink = false,
            TerminalBellStyle = TerminalBellStyle.Visual,
        });
        await svc.FlushAsync();

        var reloaded = SettingsService.Load().Current;
        Assert.Equal("/usr/bin/fish", reloaded.TerminalShellPath);
        Assert.Equal(["-i"], reloaded.TerminalShellArgs);
        Assert.Equal("Cascadia Mono", reloaded.TerminalFontFamily);
        Assert.Equal(15, reloaded.TerminalFontSize);
        Assert.Equal(12_000, reloaded.TerminalScrollbackLines);
        Assert.Equal(320, reloaded.TerminalDockHeight);
        Assert.True(reloaded.TerminalDockOpen);
        Assert.Equal(TerminalCursorStyle.Bar, reloaded.TerminalCursorStyle);
        Assert.False(reloaded.TerminalCursorBlink);
        Assert.Equal(TerminalBellStyle.Visual, reloaded.TerminalBellStyle);
    }

    [Fact]
    public void ClampTerminalScrollback_bounds()
    {
        Assert.Equal(AppSettings.MinTerminalScrollback, AppSettings.ClampTerminalScrollback(0));
        Assert.Equal(AppSettings.MaxTerminalScrollback, AppSettings.ClampTerminalScrollback(int.MaxValue));
        Assert.Equal(9000, AppSettings.ClampTerminalScrollback(9000));
    }

    // ── Tab-width mode ──

    [Fact]
    public void V9_file_loads_with_dynamic_tab_width_default()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 9, \"appTheme\": \"dark\" }");

        var svc = SettingsService.Load();

        Assert.Equal(18, svc.Current.SchemaVersion);
        Assert.Equal(TabWidthMode.Dynamic, svc.Current.TabWidthMode);
        Assert.Equal(AppSettings.DefaultFixedTabWidth, svc.Current.FixedTabWidth);
    }

    [Fact]
    public async Task Schema_v10_tab_width_settings_round_trip()
    {
        var svc = SettingsService.Load();
        await svc.UpdateAsync(s => s with { TabWidthMode = TabWidthMode.Fixed, FixedTabWidth = 240 });
        await svc.FlushAsync();

        var reloaded = SettingsService.Load().Current;
        Assert.Equal(TabWidthMode.Fixed, reloaded.TabWidthMode);
        Assert.Equal(240, reloaded.FixedTabWidth);
    }

    [Fact]
    public void ClampFixedTabWidth_bounds()
    {
        Assert.Equal(AppSettings.MinFixedTabWidth, AppSettings.ClampFixedTabWidth(0));
        Assert.Equal(AppSettings.MaxFixedTabWidth, AppSettings.ClampFixedTabWidth(int.MaxValue));
        Assert.Equal(200, AppSettings.ClampFixedTabWidth(200));
    }

    // ── #25: silent-crawl cap ceiling matches the resident-cap ceiling ──

    [Fact]
    public void ClampSilentCrawlRowCap_accepts_values_above_the_old_2M_ceiling()
    {
        Assert.Equal(AppSettings.MinSilentCrawlRowCap, AppSettings.ClampSilentCrawlRowCap(-1));
        Assert.Equal(3_000_000, AppSettings.ClampSilentCrawlRowCap(3_000_000));
        Assert.Equal(AppSettings.MaxResidentRowCap, AppSettings.MaxSilentCrawlRowCap);
        Assert.Equal(AppSettings.MaxSilentCrawlRowCap, AppSettings.ClampSilentCrawlRowCap(int.MaxValue));
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ not valid json ");
        var svc = SettingsService.Load();
        Assert.Equal(ThemeMode.System, svc.Current.AppTheme);
    }

    // ── §V10.2: v16 → v17 (silent crawl cap default 100k) ──

    [Fact]
    public void Fresh_settings_default_silent_crawl_cap_is_100k()
    {
        var svc = SettingsService.Load();
        Assert.Equal(100_000, svc.Current.SilentCrawlRowCap);
    }

    [Fact]
    public void Migration_v16_rewrites_the_old_300k_silent_crawl_default_to_100k()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 16, \"silentCrawlRowCap\": 300000 }");

        var svc = SettingsService.Load();

        Assert.Equal(18, svc.Current.SchemaVersion);
        Assert.Equal(100_000, svc.Current.SilentCrawlRowCap);
        var onDisk = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile))!;
        Assert.Equal(18, onDisk["schemaVersion"]!.GetValue<int>());
        Assert.Equal(100_000, onDisk["silentCrawlRowCap"]!.GetValue<int>());
    }

    [Fact]
    public void Migration_v16_keeps_a_user_chosen_silent_crawl_cap()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 16, \"silentCrawlRowCap\": 250000 }");

        var svc = SettingsService.Load();

        Assert.Equal(18, svc.Current.SchemaVersion);
        Assert.Equal(250_000, svc.Current.SilentCrawlRowCap);

        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 16, \"silentCrawlRowCap\": 0 }");

        svc = SettingsService.Load();

        Assert.Equal(18, svc.Current.SchemaVersion);
        Assert.Equal(0, svc.Current.SilentCrawlRowCap);
    }

    // ── v17 → v18 (show author date setting) ──

    [Fact]
    public void V17_file_loads_with_commit_date_shown()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 17, \"appTheme\": \"dark\" }");

        var svc = SettingsService.Load();

        Assert.Equal(18, svc.Current.SchemaVersion);
        Assert.False(svc.Current.ShowAuthorDate);
    }

    [Fact]
    public async Task Show_author_date_toggle_round_trips()
    {
        var svc = SettingsService.Load();
        await svc.UpdateAsync(s => s with { ShowAuthorDate = true });
        await svc.FlushAsync();

        Assert.True(SettingsService.Load().Current.ShowAuthorDate);
    }

    public void Dispose() => _state.Dispose();
}
