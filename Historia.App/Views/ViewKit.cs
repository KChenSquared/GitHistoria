namespace Historia.Views;

/// <summary>
/// Shared helpers for the hand-built Avalonia views.
/// Pure-C# construction, matching the native-control style, so there is no XAML
/// compiler in the loop for the ported panels.
/// </summary>
internal static class ViewKit
{
    /// <summary>The current user's home directory — the one place the App layer spells out
    /// <see cref="Environment.SpecialFolder.UserProfile"/> (terminal "Home" origins, the clone-root
    /// fallback, the ~/.gitconfig write). Core has its own copies for its own seams.</summary>
    public static string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Bind a control property to a theme resource key (the {DynamicResource} of code).</summary>
    public static T Dyn<T>(this T c, AvaloniaProperty p, string key) where T : Control
    {
        c.Bind(p, new DynamicResourceExtension(key));
        return c;
    }

    public static TextBlock Text(string? text, string fgKey = "ColorText", double size = 13, FontWeight weight = FontWeight.Normal)
        => new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        }.Dyn(TextBlock.ForegroundProperty, fgKey);

    public static Control Section(string title) => Text(title, "ColorTextMuted", 13, FontWeight.SemiBold);

    public static Button TextButton(string caption, Action onClick, bool primary = false)
    {
        var b = new Button
        {
            Content = caption,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        b.Classes.Add(primary ? "primary" : "secondary");
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>A bitmap from the app's embedded <c>Assets/</c> (avares:// resources).</summary>
    public static Avalonia.Media.Imaging.Bitmap? Asset(string fileName)
    {
        try
        {
            return new Avalonia.Media.Imaging.Bitmap(
                Avalonia.Platform.AssetLoader.Open(new System.Uri($"avares://Historia/Assets/{fileName}")));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Points <paramref name="img"/> at the Historia ship emblem for the current theme — the
    /// maroon <c>historia-mark-light</c> in Light mode, the copper <c>historia-mark-dark</c> in Dark
    /// and High Contrast — and keeps it in sync on <see cref="Historia.Services.Theming.IThemeService.ThemeChanged"/>.
    /// The subscription is tied to the control's visual-tree lifetime, so it leaks nothing.
    /// </summary>
    public static Image BindThemedMark(Image img, Historia.Services.Theming.IThemeService theme)
    {
        void Apply()
        {
            var light = theme.Effective == Historia.Models.EffectiveTheme.Light;
            img.Source = (light ? Asset("historia-mark-light@2x.png") : Asset("historia-mark-dark@2x.png"))
                         ?? (light ? Asset("historia-mark-light.png") : Asset("historia-mark-dark.png"));
        }

        EventHandler<Historia.Services.Theming.ThemeChangedEventArgs> onChanged =
            (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Apply);

        img.AttachedToVisualTree += (_, _) =>
        {
            theme.ThemeChanged -= onChanged;   // idempotent: guard against a re-attach doubling up
            theme.ThemeChanged += onChanged;
            Apply();
        };
        img.DetachedFromVisualTree += (_, _) => theme.ThemeChanged -= onChanged;

        theme.ThemeChanged += onChanged;       // covers the window's first paint before the attach event
        Apply();
        return img;
    }

    /// <summary>An icon glyph in <c>currentColor</c>. Material Symbols render
    /// filled on a 960-unit grid, scaled to <paramref name="size"/>.</summary>
    public static Control Icon(string name, double size = 16, string fgKey = "ColorText")
        => IconGlyphPath(name, size, fgKey);

    /// <summary>Shared <see cref="Path"/> builder for <see cref="IconPaths"/> glyphs — renders
    /// Material Symbols monochrome fill geometry on a 960-unit grid with optical padding preserved.</summary>
    public static Path IconGlyphPath(string name, double size, string fgKey)
    {
        var data = IconPaths.Get(name);
        var geometry = Geometry.Parse(data);
        geometry.Transform = new MatrixTransform(
            Matrix.CreateTranslation(0, 960) * Matrix.CreateScale(size / 960.0, size / 960.0));
        var path = new Path
        {
            Data = geometry,
            Stretch = Stretch.None,
            Width = size,
            Height = size,
        };
        return path.Dyn(Path.FillProperty, fgKey);
    }

    public static Button IconButton(string iconName, string tip, Action onClick, double size = 14)
    {
        var b = new Button
        {
            Content = Icon(iconName, size, "ColorTextMuted"),
            Padding = new Thickness(4),
            Background = Brushes.Transparent,
            BorderThickness = default,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Style class on every <see cref="HelpHint"/> badge — the handle
    /// the UI tests find it by.</summary>
    public const string HelpHintClass = "help-hint";

    /// <summary>
    /// A small circular “?” badge. Hovering shows <paramref name="text"/> in a tooltip that
    /// wraps to fit the message with no scrollbar — used in place of a sub-text line under a
    /// setting. A <c>"\n\n"</c> in the text renders as a paragraph break. The badge is not
    /// focusable, so the text is also exposed as its automation help text for screen readers.
    /// </summary>
    public static Control HelpHint(string text, double diameter = 15)
    {
        var badge = new Border
        {
            Width = diameter,
            Height = diameter,
            CornerRadius = new CornerRadius(diameter / 2),
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Help),
            Child = new TextBlock
            {
                Text = "?",
                FontSize = diameter * 0.72,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }.Dyn(TextBlock.ForegroundProperty, "ColorTextMuted"),
        }.Dyn(Border.BorderBrushProperty, "ColorBorder");
        badge.Classes.Add(HelpHintClass);
        Avalonia.Automation.AutomationProperties.SetName(badge, "Help");
        ApplyHintText(badge, text);
        ToolTip.SetShowDelay(badge, 200);
        return badge;
    }

    /// <summary>
    /// Lays a setting control out with a <see cref="HelpHint"/> to its right. A left-aligned
    /// <see cref="DockPanel"/> with the badge docked right: the badge is measured first and the
    /// setting gets what is left, so a setting wider than the column is cut short rather than
    /// pushing the badge out of it. The badge must come first in the children to be measured
    /// first, and a later sibling draws on top, so it is raised by <c>ZIndex</c> — otherwise a
    /// fixed-width box in the setting that overruns its slot covers the badge.
    /// Trap: a horizontal <see cref="StackPanel"/> measures its children with unbounded width, so
    /// in a narrow window the badge lands past the column's edge and is clipped.
    /// </summary>
    public static Control WithHint(Control setting, string hintText)
    {
        setting.VerticalAlignment = VerticalAlignment.Center;
        var badge = HelpHint(hintText);
        badge.ZIndex = 1;
        DockPanel.SetDock(badge, Dock.Right);
        var row = new DockPanel { HorizontalAlignment = HorizontalAlignment.Left, HorizontalSpacing = 6 };
        row.Children.Add(badge);
        row.Children.Add(setting);
        return row;
    }


    private static void ApplyHintText(Control badge, string text)
    {
        // A plain wrapping TextBlock sizes itself to the text (up to MaxWidth) with no
        // ScrollViewer in the way, so every message shows whole and right-sized.
        ToolTip.SetTip(badge, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 });
        Avalonia.Automation.AutomationProperties.SetHelpText(badge, text);
    }

    /// <summary>
    /// A text block with issue-key runs turned into links.
    /// URL runs open through <paramref name="launcher"/>; the rest is plain selectable text.
    /// </summary>
    public static SelectableTextBlock Autolinked(
        string? text,
        IReadOnlyList<Historia.Models.IssueAutolinkRule> rules,
        Historia.Services.UI.IExternalLauncher launcher,
        double size = 12,
        string fgKey = "ColorText")
    {
        var tb = new SelectableTextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap }
            .Dyn(SelectableTextBlock.ForegroundProperty, fgKey);
        foreach (var run in Historia.Services.Text.IssueAutolinker.Linkify(text ?? "", rules))
        {
            if (run.Url is { } url)
            {
                var link = new Avalonia.Controls.Documents.Run(run.Text) { TextDecorations = TextDecorations.Underline };
                link.Bind(Avalonia.Controls.Documents.Run.ForegroundProperty, new DynamicResourceExtension("ColorAccent"));
                tb.Inlines!.Add(link);
                // Whole-block click: cheap and good enough — runs don't take pointer input in Avalonia.
                tb.PointerPressed += (_, e) =>
                {
                    if (e.GetCurrentPoint(tb).Properties.IsLeftButtonPressed)
                        launcher.OpenUrl(url);
                };
            }
            else
            {
                tb.Inlines!.Add(new Avalonia.Controls.Documents.Run(run.Text));
            }
        }
        return tb;
    }

    /// <summary>
    /// Wraps a full-pane detail body with a top-right close button. Closing returns to the
    /// focused repository if there is one, otherwise Home. Used by the Home / Settings /
    /// Developer / Profiles panels — never the per-repository views.
    /// </summary>
    /// <param name="requireFocusedRepo">
    /// When true (the Home panel), the button is omitted entirely if no repository is focused,
    /// since closing would have nowhere to go.
    /// </param>
    /// <param name="closeTo">
    /// An explicit route for the close button (e.g. the Themes panel returns to "settings").
    /// When null, closing returns to the focused repository if there is one, otherwise Home.
    /// </param>
    public static Control ClosablePane(Control body, IServiceProvider sp, bool requireFocusedRepo = false, string? closeTo = null)
    {
        var nav = sp.GetRequiredService<INavigationBus>();
        var workspace = sp.GetRequiredService<IWorkspaceService>();

        if (requireFocusedRepo && workspace.Focused is null)
            return body;

        var close = IconButton("close", "Close", () =>
            nav.RequestNavigate(closeTo ?? (workspace.Focused is not null ? "repo" : "")), 12);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = new Thickness(0, 6, 6, 0);
        close.ZIndex = 1;

        var grid = new Grid();
        grid.Children.Add(body);
        grid.Children.Add(close);
        return grid;
    }

    /// <summary>Attach a "Reveal in file manager" + "Copy path" context menu to a row.
    /// <paramref name="absolutePath"/> is resolved lazily on click (the entry may point at a
    /// file that no longer exists — the launcher then reveals its parent folder).</summary>
    public static T WithRevealMenu<T>(this T target, Historia.Services.UI.IExternalLauncher launcher,
        Func<string> absolutePath, string revealLabel = "Open file location",
        IEnumerable<Control>? extraItems = null) where T : Control
    {
        var items = new List<Control>
        {
            MenuLeaf(revealLabel, () => launcher.RevealInFileManager(absolutePath())),
            MenuLeaf("Copy path", () => TopLevel.GetTopLevel(target)?.Clipboard?.SetTextAsync(absolutePath())),
        };
        if (extraItems is not null)
        {
            items.Add(new Separator());
            items.AddRange(extraItems);
        }
        var menu = new ContextMenu { ItemsSource = items };
        target.ContextMenu = menu;
        return target;
    }

    /// <summary>A plain <see cref="MenuItem"/> with a click handler — the code equivalent of a
    /// <c>&lt;MenuItem Header=… Click=…/&gt;</c>.</summary>
    public static MenuItem MenuLeaf(string header, Action onClick)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    /// <summary>As <see cref="MenuLeaf(string, Action)"/>, for an async click handler. The task
    /// is fire-and-forget — the handler is expected to route its own failures. <see cref="Task"/>
    /// is nullable so a <c>?.</c>-chained one-liner binds here without a warning.</summary>
    public static MenuItem MenuLeaf(string header, Func<Task?> onClick)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => _ = onClick();
        return mi;
    }

    public static Border Card(Control child, double pad = 12)
        => new Border
        {
            Child = child,
            Padding = new Thickness(pad),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
        }.Dyn(Border.BackgroundProperty, "ColorSurface").Dyn(Border.BorderBrushProperty, "ColorBorder");
}
