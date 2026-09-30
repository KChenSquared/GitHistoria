namespace Historia.Platform;

/// <summary>
/// The modal "About Historia" window: the theme-appropriate ship emblem, the product name, the
/// build version, the third-party credit lines and an OK button. Uses the same chrome as
/// <see cref="SimpleDialog"/> (owner resolution, surface colour) and takes every colour from
/// palette resource keys.
/// </summary>
internal static class AboutDialog
{
    private const int ShortShaLength = 7;

    /// <summary>The third-party attribution lines shown under the version. Licence terms require
    /// these to stay visible somewhere in the app.</summary>
    internal static IReadOnlyList<string> Credits { get; } =
    [
        "Icons: Material Symbols by Google (Apache License 2.0)",
        "Diff viewer and conflict editor adapted from SourceGit (MIT License)",
        "Editor: AvaloniaEdit · Syntax: TextMateSharp (MIT) · Diff: DiffPlex (Apache License 2.0)",
    ];

    /// <summary>
    /// Formats an assembly informational version (<c>1.0.0+&lt;commit sha&gt;</c>) for display:
    /// the text before <c>+</c>, followed by the first seven characters of the sha in
    /// parentheses when there is one. A null or empty input reads <c>Version unknown</c>.
    /// </summary>
    internal static string FormatVersion(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
            return "Version unknown";

        var plus = informational.IndexOf('+');
        var version = (plus < 0 ? informational : informational[..plus]).Trim();
        if (version.Length == 0)
            return "Version unknown";

        var sha = plus < 0 ? string.Empty : informational[(plus + 1)..].Trim();
        if (sha.Length == 0)
            return $"Version {version}";

        var shortSha = sha.Length > ShortShaLength ? sha[..ShortShaLength] : sha;
        return $"Version {version} ({shortSha})";
    }

    private static string CurrentVersionText()
    {
        var entry = System.Reflection.Assembly.GetEntryAssembly();
        var informational = entry is null
            ? null
            : System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(entry)
                ?.InformationalVersion;
        return FormatVersion(informational);
    }

    /// <summary>Shows the About window modally over the main window; completes when it closes.
    /// Safe to call from any thread.</summary>
    public static Task ShowAsync(Historia.Services.Theming.IThemeService theme) =>
        Dispatcher.UIThread.CheckAccess()
            ? ShowCoreAsync(theme)
            : Dispatcher.UIThread.InvokeAsync(() => ShowCoreAsync(theme));

    private static Task ShowCoreAsync(Historia.Services.Theming.IThemeService theme) =>
        SimpleDialog.ShowOwnedOrFree(Build(theme, CurrentVersionText()), AvaloniaDialogService.Owner);

    /// <summary>Builds the (not yet shown) About window.</summary>
    internal static Window Build(Historia.Services.Theming.IThemeService theme, string versionText)
    {
        var emblem = BindThemedMark(new Image
        {
            Width = 64,
            Height = 64,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
        }, theme);

        var name = Text("Historia", "ColorText", 22, FontWeight.SemiBold);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        name.Margin = new Thickness(0, 12, 0, 0);

        var version = new SelectableTextBlock
        {
            Text = versionText,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        }.Dyn(TextBlock.ForegroundProperty, "ColorTextMuted");

        var credits = new StackPanel { Spacing = 4, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var line in Credits)
        {
            var credit = Text(line, "ColorTextMuted", 12);
            credit.TextWrapping = TextWrapping.Wrap;
            credit.TextTrimming = TextTrimming.None;
            credit.TextAlignment = TextAlignment.Center;
            credits.Children.Add(credit);
        }

        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 80 };
        ok.Classes.Add("primary");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        buttons.Children.Add(ok);

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(emblem);
        panel.Children.Add(name);
        panel.Children.Add(version);
        panel.Children.Add(credits);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = "About Historia",
            Content = panel,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => window.Close();
        return window;
    }
}
