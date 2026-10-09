namespace HistoriaApp.Tests;

/// <summary>
/// Every explanatory note on the App Settings page is a <c>?</c> hover hint carrying its whole
/// text, never a visible caption under the control, and the labels read as approved. A private
/// caption helper shadowing <see cref="ViewKit.WithHint"/> would silently turn every hint back
/// into a caption — these tests pin the exact anchor → hint map so that cannot happen
/// unnoticed.
/// </summary>
[Trait("tier", "ui")]
public sealed class SettingsHelpHintTests
{
    private const string AppearanceHint = "Graph columns are set in Repository settings: click the cog in the graph's GRAPH header.";

    /// <summary>The Appearance hint's second paragraph, shown only in developer mode. The
    /// <c>enableDevMode</c> marker is copied next to every build of the App — this test
    /// assembly's output included — so which text applies is read from <see cref="DevMode"/>.</summary>
    private const string AppearanceDevModeParagraph = "In developer mode, a custom theme from the Developer panel overrides Light, Dark and System.";

    /// <summary>Anchor label → full hint text, for every hint on the page.</summary>
    private static readonly Dictionary<string, string> ExpectedHints = new()
    {
        ["Appearance"] = DevMode.Enabled ? AppearanceHint + "\n\n" + AppearanceDevModeParagraph : AppearanceHint,
        ["High contrast"] = "Overrides every other theme. Also turns on when Windows high contrast is on.",
        ["Wrap tabs onto more rows"] = "When tabs don't fit, they wrap to a new row instead of scrolling.",
        ["Fixed tab width"] = "When off, each tab fits its name, up to about 25 characters.",
        ["Show author date"] = "Shows when a commit was written, not when it was committed. The graph stays sorted by commit date.",
        ["Scroll direction"] = "Applies to the graph and the tab strip. On Windows, Match system is always Standard.",
        ["Cache sidebar between launches"] = "Saves each repository's branches, tags, stashes and more so the sidebar appears instantly, then refreshes from Git. Turning it off deletes the cache.",
        ["Privacy & Network"] = "Identity and SSH-only are set per repository, in Repository settings.",
        ["Auto-fetch open repositories"] = "Fetches each open repository in turn: a 5-minute pause, then one repository a minute. Turn off to stop all background network traffic.",
        ["Allow command-line control (historia-cmdr)"] = "Lets local tools and coding agents open Historia on a commit or diff, or hand you staged changes with a proposed commit message. They can't stage, commit or change your repository.",
        ["Layout"] = "Also changes when you switch layout in the diff viewer.",
        ["Context lines"] = "Unchanged lines shown around each change.",
        ["Wrap long lines"] = "Unified layout only.",
        ["Issue autolinks"] = "Turns issue keys in commit messages into links. In the URL, $1, $2… insert the pattern's capture groups."
            + "\n\nExample: pattern #(\\d+) with URL https://GitHistoria.com/autolink?issue=$1 turns #42 into a link.",
        ["Prune deleted remote branches on fetch"] = "On fetch and pull, removes remote branches that no longer exist on the server. Your local branches aren't touched.",
        ["Pull with rebase"] = "Puts your local commits on top of the remote ones instead of creating a merge commit.",
        ["Ask for a stash message"] = "When off, the Stash button names the stash \"Stash of <branch>\".",
        ["Shell arguments"] = "Quotes aren't supported — each space starts a new argument.",
        ["Scrollback (lines)"] = "Lines kept above the screen. 5,000 lines take about 5 MB per open terminal.",
        ["Enhanced keyboard (Kitty protocol)"] = "Lets editors such as Neovim and Helix tell apart keys like Ctrl+Enter and Shift+Enter. Applies to new terminals.",
        ["Allow programs to copy to the clipboard (OSC 52)"] = "Lets copy in Vim or tmux reach the system clipboard, including over SSH. Programs can never read the clipboard.",
        ["Show splash screen"] = "A custom theme uses its own splash instead.",
        ["Enable logging"] = "Records errors, every Git command and its result, repository open/close and startup timings, to the logs folder; terminal input/output isn't logged; logs older than 48 hours are deleted each auto-fetch round, and if they can't be, logging turns itself off; also on while HISTORIA_LOG is set.",
        ["Auto hide notices"] = "Closes notices in the bottom-right corner after 10 seconds. Turn off to keep them until you close them.",
        ["Keep commit details on disk"] = "Reads commit messages, authors and dates from a disk cache to save memory on large repositories. Takes effect after restart.",
        ["Fast graph updates (experimental)"] = "On large repositories, adds new commits to the saved graph instead of rebuilding it. Takes effect after restart.",
        ["Background preload (rows)"] = "Rows loaded in the background after the graph opens, and again each time you scroll past them. 0 = no limit. Takes effect after restart.",
        ["Max rows in memory"] = "Most rows the graph keeps loaded per repository. Only used when commit details are on disk; otherwise 500,000. Takes effect after restart.",
        ["Enable developer panel"] = "Adds a Developer entry to the app menu with testing and cleanup tools. Takes effect after restart.",
    };

    private static readonly string[] ApprovedLabels =
    [
        "Appearance", "Theme", "High contrast", "Wrap tabs onto more rows", "Maximum rows", "Fixed tab width",
        "Tab width (px)", "Show author date", "Scroll direction", "Match system", "Standard", "Inverted",
        "Cache sidebar between launches", "Privacy & Network", "Auto-fetch open repositories", "Allow command-line control (historia-cmdr)", "Diff viewer",
        "Layout", "Unified", "Side by side", "Context lines", "Wrap long lines", "Issue autolinks",
        "Pattern (regex)", "Link URL ($1 = first group)", "Git", "Prune deleted remote branches on fetch",
        "Pull with rebase", "Reopen tabs on launch", "Ask for a stash message", "Terminal", "Shell",
        "Shell arguments", "Separated by spaces", "Font family", "Font size", "Scrollback (lines)",
        "Cursor style", "Blinking cursor", "Advanced", "Enable logging", "Auto hide notices", "Show splash screen", "Keep commit details on disk",
        "Fast graph updates (experimental)", "Background preload (rows)", "Max rows in memory", "Enable developer panel",
    ];

    private static readonly string[] RetiredLabels =
    [
        "High Contrast mode", "Multi-line repository tabs", "Max tab rows", "Fixed-width repository tabs",
        "Show author date instead of commit date", "Match system (natural / inverted)",
        "Store a local repository cache", "Automatically fetch open repositories", "Diff & Editor",
        "Default diff view", "Side-by-Side", "Word-wrap diff lines", "pattern (regex)", "URL template",
        "Pull with rebase by default", "Reopen previous repository tabs on launch", "Use custom stash names",
        "Shell path", "(space separated)", "Scrollback lines", "Blink the cursor", "Advanced settings",
        "Show the splash screen on launch", "Read graph metadata from disk instead of memory",
        "Splice new commits into the graph cache instead of re-walking (large repositories, experimental)",
        "Background auto-load row cap", "Resident row cap",
    ];

    private sealed class FakeThemeProbe : ISystemThemeProbe
    {
        public bool IsLightTheme => false;
        public bool IsHighContrast => false;
#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067
    }

    private sealed class FakeVault : ICredentialVaultService
    {
        public Task StoreTokenAsync(string key, string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> RetrieveTokenAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteTokenAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static ServiceProvider CreateServices()
    {
        var sc = new ServiceCollection();
        var settings = SettingsService.Load();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<SettingsService>(settings);
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton<IDialogService, Historia.Platform.AvaloniaDialogService>();
        sc.AddSingleton<IScrollDirectionProbe, Historia.Platform.ScrollDirectionProbe>();
        sc.AddSingleton<IRepoActionBus, RepoActionBus>();
        sc.AddSingleton<IRepoNetworkActionRunner, RepoNetworkActionRunner>();
        sc.AddSingleton<IAutoFetchHost, AutoFetchHost>();
        sc.AddSingleton<IAutoFetchService, AutoFetchService>();
        sc.AddSingleton<Historia.Services.UI.INavigationBus, Historia.Services.UI.NavigationBus>();
        sc.AddSingleton<IKeyboardCommandRouter, KeyboardCommandRouter>();
        sc.AddSingleton<INotificationService, NotificationService>();
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton<ISystemThemeProbe, FakeThemeProbe>();
        sc.AddSingleton<IThemeService, ThemeService>();
        sc.AddSingleton<ICustomThemeService, CustomThemeService>();
        sc.AddSingleton<IWorkspaceService, WorkspaceService>();
        sc.AddSingleton<IProfileService, ProfileService>();
        sc.AddSingleton<IGitCliService, GitCliService>();
        var locator = new GitExecutableLocator();
        locator.ResolveAsync(null).GetAwaiter().GetResult();
        sc.AddSingleton<IGitExecutableLocator>(locator);
        sc.AddSingleton<IBackgroundWorkGate, BackgroundWorkGate>();
        sc.AddSingleton<IGraphLayoutService, GraphLayoutService>();
        sc.AddSingleton<Historia.Services.Storage.IRepoStateCache, Historia.Services.Storage.RepoStateCache>();
        sc.AddSingleton<IStashService, StashService>();
        sc.AddSingleton<IConflictService, ConflictService>();
        sc.AddSingleton<IPatchService, PatchService>();
        sc.AddSingleton<ICherryPickService, CherryPickService>();
        sc.AddSingleton<IRebaseService, RebaseService>();
        sc.AddSingleton<ISubmoduleService, SubmoduleService>();
        sc.AddSingleton<IWorktreeService, WorktreeService>();
        sc.AddSingleton<ISshKeyService, SshKeyService>();
        sc.AddSingleton<ICloneService, CloneService>();
        sc.AddSingleton<ITerminalBackend, PortaPtyBackend>();
        sc.AddSingleton<ITerminalSessionService, TerminalSessionService>();
        sc.AddSingleton<IRecentRepositoryService, RecentRepositoryService>();
        sc.AddSingleton<IRepoIdentityService, RepoIdentityService>();
        sc.AddSingleton<ICredentialVaultService, FakeVault>();
        sc.AddSingleton<IExternalLauncher, ExternalLauncher>();
        return sc.BuildServiceProvider();
    }

    private static void SetPrivate(SettingsView view, string field, object value)
        => (typeof(SettingsView).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"SettingsView.{field} not found — renamed?"))
            .SetValue(view, value);

    private static void Rebuild(SettingsView view)
        => (typeof(SettingsView).GetMethod("Rebuild", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("SettingsView.Rebuild not found — renamed?"))
            .Invoke(view, null);

    private static StackPanel Root(SettingsView view)
        => (StackPanel)(typeof(SettingsView).GetField("_root", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("SettingsView._root not found — renamed?")).GetValue(view)!;

    /// <summary>Runs <paramref name="body"/> with the settings changed by
    /// <paramref name="mutate"/>, then restores what was there — the settings file is shared by
    /// every test in the run.</summary>
    private static void WithSettings(ISettingsService cfg, Func<AppSettings, AppSettings> mutate, Action body)
    {
        var original = cfg.Current;
        cfg.UpdateAsync(mutate).GetAwaiter().GetResult();
        try
        {
            body();
        }
        finally
        {
            cfg.UpdateAsync(_ => original).GetAwaiter().GetResult();
        }
    }

    [AvaloniaFact]
    public void Every_app_settings_hint_has_its_full_text()
    {
        var sp = CreateServices();
        var view = new SettingsView(sp);

        var map = HelpHintMap.Of(view);

        Assert.Equal(ExpectedHints.OrderBy(p => p.Key, StringComparer.Ordinal),
                     map.OrderBy(p => p.Key, StringComparer.Ordinal));
        view.Dispose();
    }

    [AvaloniaFact]
    public void Hint_badges_expose_their_text_to_automation()
    {
        var sp = CreateServices();
        var view = new SettingsView(sp);

        var badges = HelpHintMap.Badges(view).ToList();
        Assert.NotEmpty(badges);
        foreach (var badge in badges)
        {
            Assert.Equal("Help", Avalonia.Automation.AutomationProperties.GetName(badge));
            Assert.Equal(HelpHintMap.TipText(badge), Avalonia.Automation.AutomationProperties.GetHelpText(badge));
        }
        view.Dispose();
    }

    [AvaloniaFact]
    public void App_settings_labels_match_the_approved_copy()
    {
        var sp = CreateServices();
        var cfg = sp.GetRequiredService<ISettingsService>();

        // The sub-options and the autolink watermarks only exist in these states.
        WithSettings(cfg, s => s with
        {
            EnableMultiLineTabs = true,
            TabWidthMode = TabWidthMode.Fixed,
            IssueAutolinks = [new IssueAutolinkRule { Pattern = "", UrlTemplate = "" }],
        }, () =>
        {
            var view = new SettingsView(sp);
            var labels = VisibleLabels(view);

            foreach (var label in ApprovedLabels)
                Assert.True(labels.Contains(label), $"Missing label: {label}");
            foreach (var label in RetiredLabels)
                Assert.False(labels.Contains(label), $"Retired label still shown: {label}");
            view.Dispose();
        });
    }

    [AvaloniaFact]
    public void No_wrapping_subtext_remains_on_the_app_settings_page()
    {
        var sp = CreateServices();
        var view = new SettingsView(sp);

        var tips = HelpHintMap.Badges(view).Select(ToolTip.GetTip).ToHashSet();
        var wrapping = view.GetLogicalDescendants().OfType<TextBlock>()
            .Where(t => !tips.Contains(t) && t.TextWrapping == TextWrapping.Wrap)
            .Select(t => t.Text)
            .ToList();

        Assert.Empty(wrapping);
        view.Dispose();
    }

    [AvaloniaFact]
    public void Fixed_tab_width_hint_is_present_in_both_tab_width_modes()
    {
        var sp = CreateServices();
        var cfg = sp.GetRequiredService<ISettingsService>();
        var original = cfg.Current.TabWidthMode;
        var view = new SettingsView(sp);
        try
        {
            foreach (var mode in new[] { TabWidthMode.Fixed, TabWidthMode.Dynamic })
            {
                cfg.UpdateAsync(s => s with { TabWidthMode = mode }).GetAwaiter().GetResult();
                Rebuild(view);

                var map = HelpHintMap.Of(view);
                Assert.True(map.TryGetValue("Fixed tab width", out var hint), $"No Fixed tab width hint in {mode} mode.");
                Assert.Equal(ExpectedHints["Fixed tab width"], hint);
            }
        }
        finally
        {
            cfg.UpdateAsync(s => s with { TabWidthMode = original }).GetAwaiter().GetResult();
            view.Dispose();
        }
    }

    /// <summary>The badge sits right of its setting and inside the settings column — at the
    /// normal width, and in a narrow window where a row (label + fixed-width box + badge) is
    /// wider than the column, so a layout that lets the setting push the badge out, or draw over
    /// it, fails here.</summary>
    [AvaloniaTheory]
    [InlineData(800d)]
    [InlineData(400d)]
    public void Hint_badges_stay_inside_the_settings_column(double windowWidth)
    {
        var sp = CreateServices();
        var view = new SettingsView(sp);
        SetPrivate(view, "_appearanceExpanded", true);
        SetPrivate(view, "_networkExpanded", true);
        SetPrivate(view, "_diffExpanded", true);
        SetPrivate(view, "_gitExpanded", true);
        SetPrivate(view, "_terminalExpanded", true);
        SetPrivate(view, "_advExpanded", true);
        Rebuild(view);

        var window = new Window { Width = windowWidth, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var root = Root(view);
        Assert.True(root.Bounds.Width > 0 && root.Bounds.Width <= windowWidth);

        var badges = HelpHintMap.Badges(view).ToList();
        Assert.Equal(ExpectedHints.Count, badges.Count);
        foreach (var badge in badges)
        {
            var anchor = HelpHintMap.AnchorLabel(badge);
            Assert.True(badge.IsEffectivelyVisible, $"Badge for '{anchor}' is hidden.");
            var topLeft = badge.TranslatePoint(new Point(0, 0), root)
                ?? throw new InvalidOperationException($"Badge for '{anchor}' is not under the root.");
            var right = topLeft.X + badge.Bounds.Width;
            Assert.True(badge.Bounds.Width > 0, $"Badge for '{anchor}' was not laid out.");
            Assert.True(right <= root.Bounds.Width + 0.5,
                $"Badge for '{anchor}' ends at {right:0.#}, past the column's {root.Bounds.Width:0.#} at window width {windowWidth}.");

            // Right of its setting, centred on the row.
            var row = (Control)badge.Parent!;
            var setting = row.GetLogicalChildren().OfType<Control>().Single(c => !ReferenceEquals(c, badge));
            var settingLeft = setting.TranslatePoint(new Point(0, 0), root)!.Value.X;
            Assert.True(topLeft.X > settingLeft, $"Badge for '{anchor}' is not right of its setting.");
            var badgeMid = topLeft.Y + badge.Bounds.Height / 2;
            var rowMid = row.TranslatePoint(new Point(0, row.Bounds.Height / 2), root)!.Value.Y;
            Assert.True(Math.Abs(badgeMid - rowMid) <= 1, $"Badge for '{anchor}' is not vertically centred on its row.");

            // Nothing from a setting too wide for its slot draws over the badge.
            var centre = badge.TranslatePoint(new Point(badge.Bounds.Width / 2, badge.Bounds.Height / 2), row)!.Value;
            var hit = row.InputHitTest(centre) as Visual;
            Assert.True(hit is not null && hit.GetSelfAndVisualAncestors().Contains(badge),
                $"Badge for '{anchor}' is covered by {hit?.GetType().Name ?? "nothing"} at window width {windowWidth}.");
        }

        window.Close();
        view.Dispose();
    }

    [AvaloniaFact]
    public void Appearance_offers_only_light_dark_system()
    {
        var sp = CreateServices();
        var cfg = sp.GetRequiredService<ISettingsService>();

        // Even with the custom-theme override on, the page offers no custom-theme controls.
        WithSettings(cfg, s => s with { UseCustomTheme = true, CustomThemeId = "theme-1" }, () =>
        {
            var view = new SettingsView(sp);

            var themeLabel = view.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text == "Theme");
            var row = (Panel)themeLabel.Parent!;
            var options = row.Children.OfType<Panel>().Single().Children
                .Select(c => (string)Assert.IsType<Button>(c).Content!)
                .ToList();
            Assert.Equal(new[] { "Light", "Dark", "System" }, options);

            var labels = VisibleLabels(view);
            Assert.DoesNotContain("Custom", labels);
            Assert.DoesNotContain("Manage themes", labels);
            Assert.DoesNotContain("Use custom theme", labels);
            view.Dispose();
        });
    }

    [AvaloniaFact]
    public void Splash_screen_sits_in_appearance_between_theme_and_high_contrast()
    {
        var sp = CreateServices();
        var view = new SettingsView(sp);

        var body = SectionBody(view, "Appearance");
        var order = body.Children.Select(RowLabel).ToList();
        Assert.Equal(new[] { "Theme", "Show splash screen", "High contrast" }, order.Take(3));
        Assert.DoesNotContain("Show splash screen", SectionBody(view, "Advanced").Children.Select(RowLabel));
        view.Dispose();
    }

    [AvaloniaTheory]
    [InlineData("Appearance")]
    [InlineData("Privacy & Network")]
    [InlineData("Diff viewer")]
    [InlineData("Git")]
    [InlineData("Terminal")]
    [InlineData("Advanced")]
    public void Sections_start_collapsed_and_expand_on_header_click(string title)
    {
        var sp = CreateServices();
        var view = new SettingsView(sp);
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var body = SectionBody(view, title);
        Assert.False(body.IsVisible, $"{title} should start collapsed.");

        var header = SectionHeader(view, title);
        var titleText = header.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text == title);
        var badge = HelpHintMap.Badges(header).FirstOrDefault();
        if (badge is not null)
        {
            // The ? beside the title shows its hint; pressing it doesn't toggle the section.
            Press(window, badge);
            Assert.False(body.IsVisible, $"Pressing {title}'s ? expanded the section.");
        }

        Press(window, titleText);
        Assert.True(body.IsVisible, $"{title} didn't expand.");
        Press(window, titleText);
        Assert.False(body.IsVisible, $"{title} didn't collapse again.");

        window.Close();
        view.Dispose();

        // Raised on the target and bubbled, as a real press would be, so the ? badge's own
        // handler gets the first look.
        static void Press(Window window, Control target)
        {
            var pointer = new Avalonia.Input.Pointer(1, Avalonia.Input.PointerType.Mouse, true);
            target.RaiseEvent(new PointerPressedEventArgs(target, pointer, window, new Point(1, 1), 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Issue_autolinks_sit_last_in_advanced()
    {
        var sp = CreateServices();
        var cfg = sp.GetRequiredService<ISettingsService>();

        WithSettings(cfg, s => s with
        {
            IssueAutolinks = [new IssueAutolinkRule { Pattern = "", UrlTemplate = "" }],
        }, () =>
        {
            var view = new SettingsView(sp);
            var advBody = SectionBody(view, "Advanced");
            var children = advBody.Children.ToList();

            var maxRowsIdx = children.FindIndex(c =>
                c.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "Max rows in memory"));
            Assert.True(maxRowsIdx >= 0, "Max rows in memory not found in Advanced.");

            var autolinkHeadingIdx = children.FindIndex(c =>
                c.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "Issue autolinks"));
            Assert.True(autolinkHeadingIdx > maxRowsIdx, "Issue autolinks heading should come after Max rows in memory.");

            var ruleRowIdx = children.FindIndex(c =>
                c.GetLogicalDescendants().OfType<TextBox>().Any(tb => tb.Watermark == "Pattern (regex)"));
            Assert.True(ruleRowIdx > autolinkHeadingIdx, "Rule row should come after Issue autolinks heading.");

            var addRuleIdx = children.FindIndex(c => c is Button b && (b.Content as string) == "Add rule");
            Assert.True(addRuleIdx > ruleRowIdx, "Add rule button should come after rule row.");

            var devPanelIdx = children.FindIndex(c =>
                c.GetLogicalDescendants().OfType<CheckBox>().Any(cb => (cb.Content as string) == "Enable developer panel"));
            Assert.True(devPanelIdx > addRuleIdx, "Enable developer panel should come after Add rule button.");
            Assert.Equal(children.Count - 1, devPanelIdx);

            var root = Root(view);
            Assert.DoesNotContain(root.Children, c => !ReferenceEquals(c, advBody) && c.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "Issue autolinks"));
            Assert.DoesNotContain(root.Children, c => c is Button b && (b.Content as string) == "Add rule");

            view.Dispose();
        });
    }

    [AvaloniaFact]
    public void Enable_logging_sits_first_in_advanced_and_toggles_setting()
    {
        var sp = CreateServices();
        var cfg = sp.GetRequiredService<ISettingsService>();
        WithSettings(cfg, s => s with { LoggingEnabled = true }, () =>
        {
            var view = new SettingsView(sp);
            var advBody = SectionBody(view, "Advanced");
            var firstRow = advBody.Children[0];
            Assert.Equal("Enable logging", RowLabel(firstRow));

            var box = firstRow.GetLogicalDescendants().OfType<CheckBox>().Single();
            Assert.True(box.IsChecked);

            box.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(cfg.Current.LoggingEnabled);

            view.Dispose();
        });
    }

    [AvaloniaFact]
    public void Enable_developer_panel_sits_last_in_advanced()
    {
        var sp = CreateServices();
        var view = new SettingsView(sp);
        var advBody = SectionBody(view, "Advanced");
        var lastRow = advBody.Children[^1];
        Assert.Equal("Enable developer panel", RowLabel(lastRow));
        view.Dispose();
    }

    [AvaloniaFact]
    public void Advanced_hints_start_with_logging_and_end_with_developer_panel()
    {
        var sp = CreateServices();
        var view = new SettingsView(sp);
        var advBody = SectionBody(view, "Advanced");
        var badges = HelpHintMap.Badges(advBody).ToList();
        Assert.NotEmpty(badges);
        Assert.Equal("Enable logging", HelpHintMap.AnchorLabel(badges[0]));
        Assert.Equal("Enable developer panel", HelpHintMap.AnchorLabel(badges[^1]));
        view.Dispose();
    }

    /// <summary>The clickable header of a collapsible section.</summary>
    private static Border SectionHeader(SettingsView view, string title)
        => Root(view).Children.OfType<Border>().Single(b =>
            b.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == title));

    /// <summary>The body a collapsible section's header shows and hides.</summary>
    private static StackPanel SectionBody(SettingsView view, string title)
    {
        var root = Root(view);
        return (StackPanel)root.Children[root.Children.IndexOf(SectionHeader(view, title)) + 1];
    }

    /// <summary>A section row's label: a check box's content, or a labelled row's first text,
    /// looking through a <c>?</c> hint row.</summary>
    private static string? RowLabel(Control row) => row switch
    {
        CheckBox { Content: string s } => s,
        Panel p when p.Children.Any(c => c.Classes.Contains(ViewKit.HelpHintClass))
            => RowLabel(p.Children.Single(c => !c.Classes.Contains(ViewKit.HelpHintClass))),
        Panel p => p.Children.OfType<TextBlock>().FirstOrDefault()?.Text,
        _ => null,
    };

    /// <summary>Every user-visible label string: text, control content, combo options and
    /// input watermarks.</summary>
    private static HashSet<string> VisibleLabels(Control view)
    {
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in view.GetLogicalDescendants())
        {
            switch (c)
            {
                case TextBlock t when t.Text is { } s:
                    labels.Add(s);
                    break;
                case ContentControl cc when cc.Content is string s:
                    labels.Add(s);
                    break;
                case TextBox tb when tb.Watermark is { } w:
                    labels.Add(w);
                    break;
            }
            if (c is ComboBox combo)
            {
                foreach (var item in combo.Items.OfType<ComboBoxItem>())
                    if (item.Content is string s)
                        labels.Add(s);
            }
        }
        return labels;
    }
}

/// <summary>Reads the <c>?</c> hints off a hand-built settings view: each badge, the label of
/// the setting it sits beside, and its tooltip text.</summary>
internal static class HelpHintMap
{
    public static IEnumerable<Control> Badges(Control view)
        => view.GetLogicalDescendants().OfType<Control>().Where(c => c.Classes.Contains(ViewKit.HelpHintClass));

    public static string TipText(Control badge)
        => (ToolTip.GetTip(badge) as TextBlock)?.Text
           ?? throw new InvalidOperationException("Hint badge has no TextBlock tooltip.");

    /// <summary>The label of the setting a badge sits beside: a checkbox's, toggle's or
    /// button's content, a labelled row's first text, a heading's text, or the content of the
    /// checkbox that leads a row of controls.</summary>
    public static string AnchorLabel(Control badge)
    {
        var row = badge.Parent as Panel ?? throw new InvalidOperationException("Hint badge has no row.");
        var setting = row.Children.Single(c => !ReferenceEquals(c, badge));
        return setting switch
        {
            ContentControl { Content: string s } => s,
            TextBlock { Text: { } s } => s,
            Panel p when p.Children.OfType<TextBlock>().FirstOrDefault() is { Text: { } s } => s,
            Panel p when p.Children.FirstOrDefault() is CheckBox { Content: string s } => s,
            _ => throw new InvalidOperationException($"Unrecognised hint anchor: {setting.GetType().Name}"),
        };
    }

    public static Dictionary<string, string> Of(Control view)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var badge in Badges(view))
        {
            var label = AnchorLabel(badge);
            Assert.False(map.ContainsKey(label), $"Two hints anchored on '{label}'.");
            map[label] = TipText(badge);
        }
        return map;
    }
}
