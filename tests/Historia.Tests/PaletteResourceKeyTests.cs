namespace Historia.Tests;

/// <summary>
/// <c>PaletteBrushes.Get</c> returns a transparent brush for a key the theme does not define, so a
/// stale or mistyped palette resource key blanks a colour without any error. This scans the whole
/// App source tree and fails on any <c>Color…</c> resource key that no palette token produces.
/// </summary>
[Trait("tier", "unit")]
public class PaletteResourceKeyTests
{
    /// <summary>
    /// <c>Color…</c> literals that are not palette resource keys. Empty: every such literal in the
    /// App is a palette key. An entry needs a comment naming what the literal is.
    /// </summary>
    private static readonly string[] NonPaletteLiterals = [];

    // Any quoted Color… literal in C#: PaletteBrushes.Get / GetTranslucent / Pen, the key argument of
    // .Dyn(…), and the key parameters of the view-building helpers.
    private static readonly Regex CSharpKey = new(@"""(Color[A-Z][A-Za-z0-9]*)""", RegexOptions.Compiled);

    private static readonly Regex AxamlKey = new(@"\{(?:Dynamic|Static)Resource\s+(Color[A-Za-z0-9]+)\s*\}", RegexOptions.Compiled);

    private static string AppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Historia.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "Historia.App");
    }

    private static IEnumerable<string> AppSources(string appRoot)
        => Directory.EnumerateFiles(appRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>Every (file, key) the App source names, with the file's path relative to the App root.</summary>
    private static List<(string File, string Key)> NamedKeys()
    {
        var appRoot = AppRoot();
        var found = new List<(string, string)>();
        foreach (var file in AppSources(appRoot))
        {
            var rel = Path.GetRelativePath(appRoot, file).Replace('\\', '/');
            var text = File.ReadAllText(file);
            var regex = file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) ? AxamlKey : CSharpKey;
            foreach (Match m in regex.Matches(text))
            {
                found.Add((rel, m.Groups[1].Value));
            }
        }

        return found;
    }

    [Fact]
    public void Every_palette_resource_key_in_the_app_resolves()
    {
        var known = HistoriaPalette.Tokens.Select(HistoriaPalette.ResourceKey).ToHashSet(StringComparer.Ordinal);

        var unknown = NamedKeys()
            .Where(k => !known.Contains(k.Key) && !NonPaletteLiterals.Contains(k.Key))
            .Select(k => $"{k.File}: {k.Key}")
            .Distinct()
            .ToList();

        Assert.True(unknown.Count == 0,
            "Resource key(s) that no palette token defines (they resolve to a transparent brush):\n  " + string.Join("\n  ", unknown));
    }

    [Fact]
    public void Scanner_sees_known_call_sites()
    {
        var keys = NamedKeys();

        Assert.Contains(keys, k => k.File == "Controls/Diff/DiffRenderers.cs" && k.Key == "ColorDiffAddBg");
        Assert.Contains(keys, k => k.File == "MainWindow.axaml" && k.Key == "ColorBorder");
    }
}
