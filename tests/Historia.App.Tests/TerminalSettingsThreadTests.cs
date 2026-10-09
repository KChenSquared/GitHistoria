namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public sealed class TerminalSettingsThreadTests
{
    private sealed class StubThemeService : IThemeService
    {
        public EffectiveTheme Effective { get; set; } = EffectiveTheme.Dark;
        public ThemeMode CurrentMode => ThemeMode.Dark;
        public bool HighContrastEnabled => false;
        public string EffectiveCssThemeName => "dark";
        public IReadOnlyDictionary<string, Rgb> Palette => HistoriaPalette.For(Effective);
        public IReadOnlyList<Rgb> LaneColors => [];
#pragma warning disable CS0067
        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
#pragma warning restore CS0067
        public Task ApplyModeAsync(ThemeMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public bool CustomThemeActive => false;
        public Task SetCustomThemeEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task SelectCustomThemeAsync(string themeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHighContrastAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeSettingsService(AppSettings? initial = null) : ISettingsService
    {
        private AppSettings _current = initial ?? new();
        public AppSettings Current => _current;
        public event EventHandler<SettingsChangedEventArgs>? Changed;

        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default)
        {
            var oldSettings = _current;
            var newSettings = mutate(oldSettings);
            _current = newSettings;
            Changed?.Invoke(this, new SettingsChangedEventArgs(oldSettings, newSettings));
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static (TerminalControl Control, ISettingsService Settings, Window Window) CreateAttachedControl()
    {
        var settings = new FakeSettingsService(new AppSettings { TerminalFontSize = 13 });
        var sc = new ServiceCollection();
        sc.AddSingleton<IThemeService, StubThemeService>();
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<IExternalLauncher, ExternalLauncher>();
        var sp = sc.BuildServiceProvider();

        var control = new TerminalControl();
        control.Attach(sp);

        var window = new Window { Content = control, Width = 640, Height = 384 };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        return (control, settings, window);
    }

    [AvaloniaFact]
    public async Task Font_change_from_a_background_thread_is_applied_on_the_ui_thread()
    {
        var (control, settings, window) = CreateAttachedControl();
        try
        {
            var initialSize = control.CellSize;

            var task = Task.Run(() => settings.UpdateAsync(s => s with { TerminalFontSize = 24 }));
            await task;

            Dispatcher.UIThread.RunJobs();

            Assert.NotEqual(initialSize, control.CellSize);
            Assert.True(control.CellSize.Height > initialSize.Height);
        }
        finally
        {
            window.Close();
            control.Teardown();
        }
    }

    [AvaloniaFact]
    public async Task Font_change_on_the_ui_thread_applies_immediately()
    {
        var (control, settings, window) = CreateAttachedControl();
        try
        {
            var initialSize = control.CellSize;

            await settings.UpdateAsync(s => s with { TerminalFontSize = 24 });

            Assert.NotEqual(initialSize, control.CellSize);
            Assert.True(control.CellSize.Height > initialSize.Height);
        }
        finally
        {
            window.Close();
            control.Teardown();
        }
    }

    [AvaloniaFact]
    public void Posted_font_change_after_detach_does_nothing()
    {
        var (control, settings, window) = CreateAttachedControl();
        try
        {
            using var done = new ManualResetEventSlim(false);
            Task.Run(async () =>
            {
                await settings.UpdateAsync(s => s with { TerminalFontSize = 24 });
                done.Set();
            });

            done.Wait();
            control.Teardown();
            window.Content = null;

            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            window.Close();
        }
    }
}
