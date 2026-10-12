namespace Historia.Tests;

/// <summary>
/// View markup guard. Views are AXAML bound to view models, and the palette stays the single
/// source of colour: markup names colours only through <c>{DynamicResource Color…}</c>, so a theme
/// change reaches every view live. A colour literal, a named colour or a <c>StaticResource</c>
/// palette key in markup would freeze one theme into a view without any error, and a view root
/// with no <c>x:DataType</c> would fall back to reflection bindings. This scans every
/// <c>.axaml</c> file in the App except <c>App.axaml</c>, which defines the palette resources.
/// </summary>
[Trait("tier", "unit")]
public class ViewMarkupGuardTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// Markup that legitimately names a colour outside the palette, as (file, attribute, value).
    /// An entry needs a comment saying why, and <see cref="Allowlisted_markup_literals_are_still_present"/>
    /// fails once the literal is gone.
    /// </summary>
    private static readonly (string File, string Attribute, string Value)[] AllowedNamedColours =
    [
        // The splash window shows before any theme resource dictionary is loaded; the splash art
        // is an opaque image, and this is only the window behind it.
        ("Historia.App/SplashWindow.axaml", "Background", "White"),
    ];

    /// <summary>Attributes that carry a colour or a brush.</summary>
    private const string ColourAttribute = @"[A-Za-z.]*(?:Background|Foreground|Fill|Stroke|Color|Brush)";

    private static readonly string[] NamedColours =
    [
        "AliceBlue", "AntiqueWhite", "Aqua", "Aquamarine", "Azure", "Beige", "Bisque", "Black",
        "BlanchedAlmond", "Blue", "BlueViolet", "Brown", "BurlyWood", "CadetBlue", "Chartreuse",
        "Chocolate", "Coral", "CornflowerBlue", "Cornsilk", "Crimson", "Cyan", "DarkBlue", "DarkCyan",
        "DarkGoldenrod", "DarkGray", "DarkGreen", "DarkGrey", "DarkKhaki", "DarkMagenta",
        "DarkOliveGreen", "DarkOrange", "DarkOrchid", "DarkRed", "DarkSalmon", "DarkSeaGreen",
        "DarkSlateBlue", "DarkSlateGray", "DarkSlateGrey", "DarkTurquoise", "DarkViolet", "DeepPink",
        "DeepSkyBlue", "DimGray", "DimGrey", "DodgerBlue", "Firebrick", "FloralWhite", "ForestGreen",
        "Fuchsia", "Gainsboro", "GhostWhite", "Gold", "Goldenrod", "Gray", "Green", "GreenYellow",
        "Grey", "Honeydew", "HotPink", "IndianRed", "Indigo", "Ivory", "Khaki", "Lavender",
        "LavenderBlush", "LawnGreen", "LemonChiffon", "LightBlue", "LightCoral", "LightCyan",
        "LightGoldenrodYellow", "LightGray", "LightGreen", "LightGrey", "LightPink", "LightSalmon",
        "LightSeaGreen", "LightSkyBlue", "LightSlateGray", "LightSlateGrey", "LightSteelBlue",
        "LightYellow", "Lime", "LimeGreen", "Linen", "Magenta", "Maroon", "MediumAquamarine",
        "MediumBlue", "MediumOrchid", "MediumPurple", "MediumSeaGreen", "MediumSlateBlue",
        "MediumSpringGreen", "MediumTurquoise", "MediumVioletRed", "MidnightBlue", "MintCream",
        "MistyRose", "Moccasin", "NavajoWhite", "Navy", "OldLace", "Olive", "OliveDrab", "Orange",
        "OrangeRed", "Orchid", "PaleGoldenrod", "PaleGreen", "PaleTurquoise", "PaleVioletRed",
        "PapayaWhip", "PeachPuff", "Peru", "Pink", "Plum", "PowderBlue", "Purple", "Red",
        "RosyBrown", "RoyalBlue", "SaddleBrown", "Salmon", "SandyBrown", "SeaGreen", "SeaShell",
        "Sienna", "Silver", "SkyBlue", "SlateBlue", "SlateGray", "SlateGrey", "Snow", "SpringGreen",
        "SteelBlue", "Tan", "Teal", "Thistle", "Tomato", "Turquoise", "Violet", "Wheat", "White",
        "WhiteSmoke", "Yellow", "YellowGreen",
    ];

    private static readonly string NamedColourAlternation = string.Join("|", NamedColours);

    // Attribute="NamedColour" on a colour-carrying attribute.
    private static readonly Regex NamedColourAttribute = new(
        $@"(?<![\w.])(?<attr>{ColourAttribute})\s*=\s*""\s*(?<value>{NamedColourAlternation})\s*""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // <Setter Property="Background" Value="NamedColour" />.
    private static readonly Regex NamedColourSetter = new(
        $@"Property\s*=\s*""(?<attr>[A-Za-z.]*(?:Background|Foreground|Fill|Stroke|Color|Brush))""\s+Value\s*=\s*""\s*(?<value>{NamedColourAlternation})\s*""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A whole attribute value, or a whole element text, that is #rgb, #argb, #rrggbb or #aarrggbb.
    // Anything else starting with # (an element-name binding such as {Binding #Root.Width}) is not a colour.
    private static readonly Regex HexLiteral = new(
        @"(?:=\s*""\s*|>\s*)(?<hex>#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{3,4}))\s*(?:""|<)",
        RegexOptions.Compiled);

    private static readonly Regex StaticColourKey = new(
        @"\{\s*StaticResource\s+Color",
        RegexOptions.Compiled);

    private static readonly Regex XmlComment = new(@"<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void View_markup_uses_only_palette_resources_and_declares_its_data_type()
    {
        var root = RepoRoot();
        var violations = new List<string>();

        foreach (var file in MarkupFiles(root))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            violations.AddRange(FindViolations(rel, File.ReadAllText(file)));
        }

        Assert.True(violations.Count == 0,
            "View markup violation — colours come from {DynamicResource Color…} only, and every view root declares x:DataType:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void Scanner_covers_the_app_markup()
    {
        var root = RepoRoot();
        var scanned = MarkupFiles(root)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToList();

        Assert.Contains("Historia.App/MainWindow.axaml", scanned);
        Assert.DoesNotContain("Historia.App/App.axaml", scanned);
    }

    [Fact]
    public void Allowlisted_markup_literals_are_still_present()
    {
        var root = RepoRoot();
        foreach (var (file, attribute, value) in AllowedNamedColours)
        {
            var text = StripComments(File.ReadAllText(Path.Combine(root, file)));
            var present = NamedColourAttribute.Matches(text)
                .Any(m => string.Equals(m.Groups["attr"].Value, attribute, StringComparison.Ordinal)
                          && string.Equals(m.Groups["value"].Value, value, StringComparison.OrdinalIgnoreCase));
            Assert.True(present,
                $"{file} no longer sets {attribute}=\"{value}\"; remove it from AllowedNamedColours.");
        }
    }

    [Theory]
    [InlineData("""<Border Background="#fff" />""")]
    [InlineData("""<Border Background="#1e1e1e" />""")]
    [InlineData("""<Border Background="#ff1e1e1e" />""")]
    [InlineData("""<SolidColorBrush Color="#80ffffff"></SolidColorBrush>""")]
    [InlineData("<Color>#112233</Color>")]
    public void Colour_literals_are_reported(string markup)
    {
        var violations = FindViolations("Historia.App/Views/Fixture.axaml", Wrap(markup));

        Assert.Single(violations);
        Assert.Contains("colour literal", violations[0]);
    }

    [Theory]
    [InlineData("""<Border Background="Red" />""")]
    [InlineData("""<TextBlock Foreground="white" />""")]
    [InlineData("""<Path Fill="CornflowerBlue" Stroke="Black" />""")]
    [InlineData("""<Setter Property="Background" Value="Gold" />""")]
    public void Named_colours_are_reported(string markup)
    {
        var violations = FindViolations("Historia.App/Views/Fixture.axaml", Wrap(markup));

        Assert.NotEmpty(violations);
        Assert.All(violations, v => Assert.Contains("named colour", v));
    }

    [Fact]
    public void Static_resource_palette_keys_are_reported()
    {
        var violations = FindViolations("Historia.App/Views/Fixture.axaml",
            Wrap("""<Border Background="{StaticResource ColorBg}" />"""));

        Assert.Single(violations);
        Assert.Contains("StaticResource", violations[0]);
    }

    [Theory]
    [InlineData("""<Border Background="{DynamicResource ColorBg}" />""")]
    [InlineData("""<Border Background="Transparent" />""")]
    [InlineData("""<TextBlock Text="Red" Name="Gold" />""")]
    [InlineData("""<TextBlock Text="{Binding #Add.Text}" />""")]
    [InlineData("""<!-- <Border Background="#fff" /> -->""")]
    [InlineData("""<Border BorderBrush="{DynamicResource ColorBorder}" Background="{StaticResource DividerBrush}" />""")]
    public void Palette_resources_and_unrelated_text_are_accepted(string markup)
    {
        Assert.Empty(FindViolations("Historia.App/Views/Fixture.axaml", Wrap(markup)));
    }

    [Theory]
    [InlineData("Historia.App/Views/Fixture.axaml", "UserControl")]
    [InlineData("Historia.App/Platform/Fixture.axaml", "Window")]
    public void A_view_root_without_a_data_type_is_reported(string rel, string rootElement)
    {
        var markup = $"""<{rootElement} xmlns="https://github.com/avaloniaui"><Border /></{rootElement}>""";

        var violations = FindViolations(rel, markup);

        Assert.Single(violations);
        Assert.Contains("x:DataType", violations[0]);
    }

    [Theory]
    [InlineData("Historia.App/Views/Fixture.axaml", "UserControl", true)]
    [InlineData("Historia.App/MainWindow.axaml", "Window", false)]
    [InlineData("Historia.App/Views/Styles/Fixture.axaml", "Styles", false)]
    public void A_declared_data_type_or_a_non_view_root_is_accepted(string rel, string rootElement, bool declare)
    {
        var dataType = declare ? " x:DataType=\"vm:FixtureViewModel\"" : "";
        var markup = $"""<{rootElement} xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:vm="clr-namespace:Historia.ViewModels"{dataType}><Border /></{rootElement}>""";

        Assert.Empty(FindViolations(rel, markup));
    }

    private static string Wrap(string inner) =>
        $"""<UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:DataType="x:Object">{inner}</UserControl>""";

    private static string StripComments(string text) => XmlComment.Replace(text, " ");

    /// <summary>Every rule broken by the markup in <paramref name="text"/>, as readable lines.</summary>
    private static List<string> FindViolations(string rel, string text)
    {
        var violations = new List<string>();
        var markup = StripComments(text);

        foreach (Match m in HexLiteral.Matches(markup))
            violations.Add($"{rel}:{LineOf(markup, m.Index)}: colour literal {m.Groups["hex"].Value} — use {{DynamicResource Color…}}.");

        foreach (var regex in new[] { NamedColourAttribute, NamedColourSetter })
        {
            foreach (Match m in regex.Matches(markup))
            {
                var attribute = m.Groups["attr"].Value;
                var value = m.Groups["value"].Value;
                if (AllowedNamedColours.Any(a => a.File == rel
                        && string.Equals(a.Attribute, attribute, StringComparison.Ordinal)
                        && string.Equals(a.Value, value, StringComparison.OrdinalIgnoreCase)))
                    continue;
                violations.Add($"{rel}:{LineOf(markup, m.Index)}: named colour {attribute}=\"{value}\" — use {{DynamicResource Color…}}.");
            }
        }

        foreach (Match m in StaticColourKey.Matches(markup))
            violations.Add($"{rel}:{LineOf(markup, m.Index)}: {{StaticResource Color…}} — a palette key must be a {{DynamicResource}} so theme changes reach the view.");

        if (IsViewRoot(rel) && RootNeedsDataType(markup))
            violations.Add($"{rel}: the root element has no x:DataType.");

        return violations;
    }

    private static bool IsViewRoot(string rel) =>
        rel.StartsWith("Historia.App/Views/", StringComparison.Ordinal)
        || rel.StartsWith("Historia.App/Platform/", StringComparison.Ordinal);

    private static bool RootNeedsDataType(string markup)
    {
        XElement root;
        try
        {
            root = XDocument.Parse(markup).Root!;
        }
        catch (System.Xml.XmlException)
        {
            return false; // not well-formed markup; the build reports it
        }

        return root.Name.LocalName is "UserControl" or "Window" && root.Attribute(Xaml + "DataType") is null;
    }

    private static int LineOf(string text, int index) => text[..index].Count(c => c == '\n') + 1;

    private static IEnumerable<string> MarkupFiles(string root)
    {
        var appDir = Path.Combine(root, "Historia.App");
        foreach (var file in Directory.EnumerateFiles(appDir, "*.axaml", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel == "Historia.App/App.axaml")
                continue;
            yield return file;
        }
    }

    private static string RepoRoot()
    {
        // A worktree's top-level .git is a file, not a directory.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var gitEntry = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitEntry) || File.Exists(gitEntry))
                break;
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
