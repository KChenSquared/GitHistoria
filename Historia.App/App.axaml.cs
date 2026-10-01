namespace Historia;

/// <summary>
/// Composition root and startup sequence. One <see cref="IServiceProvider"/> is built here and handed to the window.
/// </summary>
public sealed class App : Application
{
    private ServiceProvider? _provider;
    private SettingsService? _settings;
    private AvaloniaThemeApplier? _themeApplier;
    private CancellationTokenSource? _singleInstanceCts;
    private SplashWindow? _splash;
    private MainWindow? _mainWindow;

    // Avalonia can raise ShutdownRequested/Exit more than once for a single quit. A second
    // pass would re-cancel/re-dispose everything below, throwing ObjectDisposedException on
    // _singleInstanceCts.Cancel() -- unhandled there, it gets swallowed by the global dispatcher
    // exception handler, which leaves the process alive but stuck forever, never actually
    // exiting. Guard so only the first pass does any of this.
    private int _exiting;

    /// <summary>How long the splash stays up before the shell is revealed. The shell
    /// (and the focused repo's commit graph) load in the background during this window. 2 s for
    /// the built-in themes; a custom theme sets its own 1–10 s (0 disables the splash).</summary>
    private TimeSpan _splashDuration = TimeSpan.FromSeconds(2);

    public IServiceProvider Services =>
        _provider ?? throw new InvalidOperationException("Service provider not built yet.");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Historia.Diagnostics.PerfLog.Mark("App.OnFrameworkInitializationCompleted start");

        // Settings are loaded synchronously so the correct theme is applied on first paint
        // with no flash — the one permitted blocking read.
        AppPaths.EnsureCreated();
        AppLog.Write(AppLogLevel.Info, "app", $"start — pid {Environment.ProcessId}, {Environment.OSVersion}");
        _settings = SettingsService.Load();

        var services = new ServiceCollection();
        ConfigureServices(services, _settings);
        _provider = services.BuildServiceProvider();

        RegisterGlobalExceptionHandlers();

        // Apply the resolved theme before the window shows.
        _themeApplier = new AvaloniaThemeApplier(_provider.GetRequiredService<IThemeService>());

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _mainWindow = new MainWindow(_provider);
            Historia.Diagnostics.PerfLog.Mark("MainWindow constructed");
            desktop.ShutdownRequested += OnShutdownRequested;
            desktop.Exit += (_, _) =>
            {
                if (System.Threading.Interlocked.Exchange(ref _exiting, 1) == 1)
                {
                    Historia.Diagnostics.PerfLog.Mark("desktop.Exit re-entrant call ignored");
                    return;
                }

                Historia.Diagnostics.PerfLog.Mark("desktop.Exit start");
                AppLog.Write(AppLogLevel.Info, "app", "exit");
                _singleInstanceCts?.Cancel();
                Historia.Diagnostics.PerfLog.Mark("desktop.Exit singleInstanceCts cancelled");
                _singleInstanceCts?.Dispose();
                Historia.Diagnostics.PerfLog.Mark("desktop.Exit singleInstanceCts disposed");
                _themeApplier?.Dispose();
                Historia.Diagnostics.PerfLog.Mark("desktop.Exit themeApplier disposed");
                // The container's synchronous Dispose() refuses outright (throws
                // InvalidOperationException) the moment it reaches a singleton that implements
                // only IAsyncDisposable -- WorkspaceService, ProfileService,
                // RecentRepositoryService and RepoLayoutService all qualify here, since they only
                // need to flush an AtomicFileWriter on teardown. DisposeAsync() is the one that
                // actually bridges both kinds correctly; block on it here since this handler has
                // to run synchronously to the end of the exit sequence. Still wrapped in
                // try/catch and timed so a throw (or an unexpectedly long wait, e.g. a
                // RepositoryContext's bounded 5s gate wait per open tab) is visible in the log
                // rather than silently swallowing whatever went wrong on quit.
                var disposeStart = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    if (_provider is not null)
                    {
                        var disposeTask = _provider.DisposeAsync().AsTask();
                        if (!disposeTask.Wait(TimeSpan.FromSeconds(5)))
                        {
                            Historia.Diagnostics.PerfLog.Mark(
                                $"desktop.Exit provider.DisposeAsync() timed out after 5s");
                        }
                        else
                        {
                            Historia.Diagnostics.PerfLog.Mark(
                                $"desktop.Exit provider disposed ({System.Diagnostics.Stopwatch.GetElapsedTime(disposeStart).TotalMilliseconds:F1}ms)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Historia.Diagnostics.PerfLog.Mark(
                        $"desktop.Exit provider.Dispose() THREW after {System.Diagnostics.Stopwatch.GetElapsedTime(disposeStart).TotalMilliseconds:F1}ms: {ex}");
                }

                // Both logs write from their own background threads; let the last lines land
                // before the process ends.
                AppLog.Flush(TimeSpan.FromSeconds(2));
                Historia.Diagnostics.PerfLog.Flush(TimeSpan.FromSeconds(1));
            };

            var splash = ResolveSplash();
            if (splash.Enabled)
            {
                // The shell is only shown once the splash finishes, so keep the process alive
                // while the splash is the sole window. The normal policy is restored on reveal.
                // Constructing the shell above already wired the commit-graph control to the
                // workspace, so the focused repo's graph computes in the background meanwhile.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _splashDuration = splash.Duration;
                _splash = new SplashWindow(splash.Dark, splash.ImageUri);
                _splash.Show();
                _ = RevealShellAfterSplashAsync(desktop);
            }
            else
            {
                // Splash disabled — assign MainWindow so the framework shows it right away.
                desktop.MainWindow = _mainWindow;
                Historia.Diagnostics.PerfLog.Mark("MainWindow assigned (no splash)");
            }
        }

        base.OnFrameworkInitializationCompleted();

        // Listen for later launches handing us their repo paths, and honour any path on
        // our own command line.
        if (Program.IsPrimaryInstance)
        {
            _singleInstanceCts = new CancellationTokenSource();
            _ = SingleInstance.RunServerAsync(OnForwardedArguments, _singleInstanceCts.Token);
        }
        OnForwardedArguments(NonSwitchArgs(Environment.GetCommandLineArgs().Skip(1)));

        _ = StartBackgroundInitializationAsync();
    }

    private static string[] NonSwitchArgs(IEnumerable<string> args) =>
        args.Where(a => !string.IsNullOrEmpty(a) && !a.StartsWith('-')).ToArray();

    /// <summary>
    /// A repo path (or several) arrived — from our own command line or forwarded by a second
    /// launch. Open each existing one and bring the window forward. An empty set just raises
    /// the window (a bare second launch).
    /// </summary>
    private void OnForwardedArguments(string[] paths)
    {
        Dispatcher.UIThread.Post(async void () =>
        {
            try
            {
                var workspace = _provider?.GetService<IWorkspaceService>();
                if (workspace is not null)
                {
                    foreach (var path in paths)
                    {
                        try
                        {
                            if (Directory.Exists(path))
                                await workspace.OpenAsync(path);
                        }
                        catch
                        {
                            // A bad path from a CLI arg isn't worth a notification.
                        }
                    }
                }
            }
            finally
            {
                ActivateMainWindow();
            }
        });
    }

    private void ActivateMainWindow()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } w })
        {
            if (w.WindowState == WindowState.Minimized)
                w.WindowState = WindowState.Normal;
            w.Activate();
        }
    }

    private void ConfigureServices(IServiceCollection services, SettingsService settings)
    {
        // ── Platform seams ──
        services.AddSingleton<IAppLog>(AppLog.Current);
        services.AddSingleton<IUiDispatcher, UiDispatcher>();
        services.AddSingleton<ISystemThemeProbe, PlatformSystemThemeProbe>();
        services.AddSingleton<IDialogService, AvaloniaDialogService>();
        services.AddSingleton<IExternalLauncher, ExternalLauncher>();
        services.AddSingleton<IScrollDirectionProbe, Historia.Platform.ScrollDirectionProbe>();
        services.AddSingleton<IRepoActionBus, RepoActionBus>();
        services.AddSingleton<IRepoNetworkActionRunner, RepoNetworkActionRunner>();
        services.AddSingleton<Historia.Services.AutoFetch.IAutoFetchHost, AutoFetchHost>();
        services.AddSingleton<Historia.Services.AutoFetch.IAutoFetchService, Historia.Services.AutoFetch.AutoFetchService>();

        // ── Singletons: app-lifetime, no repository affinity ──
        services.AddSingleton<ISettingsService>(settings);
        services.AddSingleton<SettingsService>(settings);
        services.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<ICustomThemeService, CustomThemeService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<SyntaxThemeHost>();
        services.AddSingleton<IGitExecutableLocator, GitExecutableLocator>();
        services.AddSingleton<IGitCliService, GitCliService>();
        services.AddSingleton<IBackgroundWorkGate, BackgroundWorkGate>();
        services.AddSingleton<Historia.Services.Graph.IGraphLayoutService, Historia.Services.Graph.GraphLayoutService>();
        services.AddSingleton<IRepoStateCache, RepoStateCache>();
        services.AddSingleton<Historia.Services.PowerTools.IConflictService, Historia.Services.PowerTools.ConflictService>();
        services.AddSingleton<Historia.Services.PowerTools.IPatchService, Historia.Services.PowerTools.PatchService>();
        services.AddSingleton<Historia.Services.PowerTools.IStashService, Historia.Services.PowerTools.StashService>();
        services.AddSingleton<Historia.Services.PowerTools.ICherryPickService, Historia.Services.PowerTools.CherryPickService>();
        services.AddSingleton<Historia.Services.PowerTools.IRebaseService, Historia.Services.PowerTools.RebaseService>();
        services.AddSingleton<Historia.Services.PowerTools.ISubmoduleService, Historia.Services.PowerTools.SubmoduleService>();
        services.AddSingleton<Historia.Services.PowerTools.IWorktreeService, Historia.Services.PowerTools.WorktreeService>();
        services.AddSingleton<Historia.Services.Profiles.ICredentialVaultService, Historia.Services.Profiles.CredentialVaultService>();
        services.AddSingleton<Historia.Services.Profiles.IProfileService, Historia.Services.Profiles.ProfileService>();
        services.AddSingleton<Historia.Services.Profiles.ISshKeyService, Historia.Services.Profiles.SshKeyService>();
        services.AddSingleton<Historia.Services.Profiles.IRepoIdentityService, Historia.Services.Profiles.RepoIdentityService>();
        services.AddSingleton<IRecentRepositoryService, RecentRepositoryService>();
        services.AddSingleton<IWorkspaceService, WorkspaceService>();
        services.AddSingleton<ICloneService, CloneService>();
        services.AddSingleton<IKeyboardCommandRouter, KeyboardCommandRouter>();
        services.AddSingleton<INavigationBus, NavigationBus>();
        services.AddSingleton<IUnsavedWorkRegistry, UnsavedWorkRegistry>();
        services.AddSingleton<ConflictResolverHost>();

        // ── Terminal. Singletons — never AddScoped. ──
        services.AddSingleton<Historia.Services.Terminal.ITerminalBackend, Historia.Services.Terminal.PortaPtyBackend>();
        services.AddSingleton<Historia.Services.Terminal.ITerminalSessionService, Historia.Services.Terminal.TerminalSessionService>();

        // ── Per-repository context: constructed and owned by WorkspaceService. ──
    }

    /// <summary>
    /// Pick the launch splash. A custom theme (not overridden by High Contrast) uses its own
    /// image and its 0–10 s duration, where 0 means "no splash". Every built-in theme uses the
    /// <see cref="AppSettings.ShowSplashScreen"/> toggle with the 2 s light / dark splash.
    /// </summary>
    private (bool Enabled, bool Dark, string ImageUri, TimeSpan Duration) ResolveSplash()
    {
        var sp = _provider!;
        var theme = sp.GetRequiredService<IThemeService>();

        if (theme.CurrentMode == Historia.Models.ThemeMode.Custom && !theme.HighContrastEnabled
            && sp.GetRequiredService<Historia.Services.Theming.ICustomThemeService>()
                 .Get(_settings!.Current.CustomThemeId ?? "") is { } ct)
        {
            var secs = Math.Clamp(ct.SplashSeconds, 0, 10);
            var dark = ct.BaseTheme == Historia.Models.EffectiveTheme.Dark;
            var uri = string.IsNullOrWhiteSpace(ct.SplashImagePath)
                ? Historia.Models.CustomTheme.BuiltInSplashLocation(ct.Base)
                : ct.SplashImagePath!;
            return (secs > 0, dark, uri, TimeSpan.FromSeconds(secs));
        }

        var isDark = theme.Effective == Historia.Models.EffectiveTheme.Dark;
        return (_settings!.Current.ShowSplashScreen, isDark,
            Historia.Models.CustomTheme.BuiltInSplashLocation(isDark ? "Dark" : "Light"),
            TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Hold the splash for <see cref="_splashDuration"/>, then reveal the shell that has been
    /// loading out of sight and dismiss the splash. The <c>finally</c> guarantees the shell is
    /// shown even if the delay is interrupted.
    /// </summary>
    private async Task RevealShellAfterSplashAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            await Task.Delay(_splashDuration).ConfigureAwait(false);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_mainWindow is { } main)
                {
                    desktop.MainWindow = main;
                    desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                    main.Show();
                    main.Activate();
                    Historia.Diagnostics.PerfLog.Mark("MainWindow shown (splash dismissed)");
                }
                _splash?.Close();
                _splash = null;
            });
        }
    }

    private async Task StartBackgroundInitializationAsync()
    {
        var locator = Services.GetRequiredService<IGitExecutableLocator>();
        var notifications = Services.GetRequiredService<INotificationService>();
        var settings = Services.GetRequiredService<ISettingsService>();

        // Resolving the git executable and restoring session tabs run concurrently; CLI operations
        // wait for git resolution via IGitCliService.WhenResolvedAsync. Restoring the focused tab
        // is what starts its commit-graph walk (CommitGraphControl.Bind → ComputeLayoutAsync);
        // running them concurrently lets tab restoration start immediately.
        var resolveGit = ResolveGitAsync(locator, settings, notifications);
        var restoreTabs = settings.Current.RestoreSessionTabs
            ? RestoreSessionTabsAsync(Services.GetRequiredService<IWorkspaceService>(), notifications)
            : Task.CompletedTask;
        await Task.WhenAll(resolveGit, restoreTabs).ConfigureAwait(false);

        // refresh the reserved global profile's identity from ~/.gitconfig once git is resolved.
        try { await Services.GetRequiredService<Historia.Services.Profiles.IProfileService>().SyncGlobalIdentityAsync().ConfigureAwait(false); }
        catch { /* a missing or unreadable ~/.gitconfig just leaves the global profile blank */ }

        // start auto-fetch after restore has populated the tab strip, so the "open repos
        // present" branch sees them and waits for all to fully load before [InitialFetch].
        try { Services.GetRequiredService<Historia.Services.AutoFetch.IAutoFetchService>().Start(); }
        catch { /* auto-fetch is best-effort and must never block startup */ }
    }

    private static async Task ResolveGitAsync(IGitExecutableLocator locator, ISettingsService settings, INotificationService notifications)
    {
        try
        {
            var git = await locator.ResolveAsync(settings.Current.GitExecutablePath).ConfigureAwait(false);
            Historia.Diagnostics.PerfLog.Mark($"Git resolution complete: found={git.Found}, path={git.Path}");
            // The locator reports a git below the version floor as not found; its reason names
            // the found and required versions.
            if (!git.Found)
            {
                var reason = git.Path is not null && git.NotFoundReason is { } r
                    ? r
                    : "Git was not found on this system.";
                notifications.Publish(NotificationSeverity.Warning,
                    $"{reason} Network and history operations are disabled until a supported git is installed or its path is set in Settings.");
            }
        }
        catch (Exception ex)
        {
            notifications.Publish(NotificationSeverity.Error, "Failed to locate git.", ex.NotificationDetails());
        }
    }

    private static async Task RestoreSessionTabsAsync(IWorkspaceService workspace, INotificationService notifications)
    {
        try
        {
            await workspace.RestoreSessionAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            notifications.Publish(NotificationSeverity.Error, "Failed to restore the previous session.", ex.NotificationDetails());
        }
    }

    /// <summary>
    /// Flush pending settings before the process exits.
    /// </summary>
    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        using var _ = Historia.Diagnostics.PerfLog.Scope("OnShutdownRequested settings flush");
        try { _settings?.FlushAsync().GetAwaiter().GetResult(); } catch { /* best effort */ }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            AppLog.Write(AppLogLevel.Error, "unhandled", "Dispatcher.UIThread.UnhandledException", args.Exception);
            TryNotifyFatal(args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                AppLog.Write(AppLogLevel.Error, "unhandled",
                    $"AppDomain.UnhandledException (terminating={args.IsTerminating})", ex);
                // The process is about to end: the logs write from background threads, so wait
                // (bounded) for this entry to reach the file.
                if (args.IsTerminating)
                {
                    AppLog.Flush(TimeSpan.FromSeconds(2));
                    Historia.Diagnostics.PerfLog.Flush(TimeSpan.FromSeconds(1));
                }
                TryNotifyFatal(ex);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Write(AppLogLevel.Error, "unhandled", "TaskScheduler.UnobservedTaskException", args.Exception);
            TryNotifyFatal(args.Exception);
            args.SetObserved();
        };
    }

    private void TryNotifyFatal(Exception ex)
    {
        try
        {
            _provider?.GetService<INotificationService>()?.Publish(
                NotificationSeverity.Error, "An unexpected error occurred.", ex.NotificationDetails());
        }
        catch
        {
            // Never rethrow from the last-chance handler.
        }
    }
}
