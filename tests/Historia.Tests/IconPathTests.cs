namespace Historia.Tests;

/// <summary>
/// §V2-15.1: every name referenced by an <c>&lt;Icon Name="…"&gt;</c> in any <c>.razor</c>
/// resolves in <see cref="IconPaths"/>, and no <see cref="IconPaths"/> entry is dead — except
/// the handful reserved for later V2 milestones (§V2-9.2 enumerates the full needed set).
/// </summary>
public class IconPathTests
{
    // Glyphs reserved until remaining views or affordances consume them.
    private static readonly HashSet<string> ReservedForLaterMilestones =
        new(StringComparer.Ordinal)
        {
            "chevron-right", "chevron-down", "stash", "pencil", "folder", "package", "lock", "unlock",
            "folder-open", "folder-search", "folder-tree", "git-branch", "git-merge",
            "git-pull-request", "git-commit-horizontal", "plus", "download", "arrow-down",
            "arrow-up", "arrow-down-to-line", "arrow-up-from-line", "check", "square",
            "square-check", "list", "key", "key-round", "copy", "github", "refresh-cw",
            // The "stash all" save-check glyph — consumed by the staging panel and the
            // shortcuts bar.
            "save-check",
            // Glyph kept for network affordance.
            "globe",
        };

    [Fact]
    public void EveryReferencedIconResolves()
    {
        var referenced = ReferencedIconNames();
        Assert.NotEmpty(referenced);

        var missing = referenced.Where(n => !IconPaths.Has(n)).OrderBy(n => n).ToList();
        Assert.True(missing.Count == 0, "Icon names referenced but not defined: " + string.Join(", ", missing));
    }

    [Fact]
    public void NoDeadIconPaths()
    {
        var referenced = ReferencedIconNames();
        var dead = IconPaths.Names
            .Where(n => !referenced.Contains(n) && !ReservedForLaterMilestones.Contains(n))
            .OrderBy(n => n)
            .ToList();

        Assert.True(dead.Count == 0, "IconPaths entries never referenced: " + string.Join(", ", dead));
    }

    [Fact]
    public void EveryGlyphHasPlausiblePathData()
    {
        foreach (var name in IconPaths.Names)
        {
            var d = IconPaths.Get(name);
            Assert.False(string.IsNullOrWhiteSpace(d), $"'{name}' has empty path data");
            Assert.True(d.TrimStart().StartsWith("M") || d.TrimStart().StartsWith("m"),
                $"'{name}' path data does not start with a move command: {d}");
            Assert.DoesNotContain("http", d, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void LucideGlyphsAreVendoredAndStroked()
    {
        // The generated partial must have populated the Lucide set, and those glyphs
        // render stroke-based (Rule 29 — monochrome in currentColor either way).
        Assert.True(IconPaths.IsStroke("git-branch"));
        Assert.True(IconPaths.IsStroke("folder"));      // aliased onto a Lucide glyph
        Assert.False(IconPaths.IsStroke("save"));       // Historia-authored badge fill silhouette
        Assert.True(IconPaths.Has("copy") && IconPaths.Has("github") && IconPaths.Has("plus"));
    }

    private static HashSet<string> ReferencedIconNames()
    {
        var root = RepoRoot();
        var names = new HashSet<string>(StringComparer.Ordinal);
        // Icon element name attributes in .razor; IconPaths lookups in native .cs
        // (§V3-18.2 Q8, rule 0.6 / 36).
        var rxRazor = new Regex("<Icon\\s+[^>]*?Name=\"([a-z-]+)\"", RegexOptions.IgnoreCase);
        // .cs call sites are IconPaths.Get("x") (native controls) and the ViewKit helpers
        // Icon("x") / IconButton("x", ...) / IconGlyphPath("x", ...).
        var rxCs = new Regex("(?:IconPaths\\.Get|\\bIcon(?:Button|GlyphPath)?)\\(\"([a-z-]+)\"");

        foreach (var pattern in new[] { "*.razor", "*.cs" })
        foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Contains("/bin/") || rel.Contains("/obj/")
                || rel.Contains("/tests/") || rel.StartsWith("tests/", StringComparison.Ordinal))
                continue;
            var text = File.ReadAllText(file);
            var rx = file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ? rxRazor : rxCs;
            foreach (Match m in rx.Matches(text))
                names.Add(m.Groups[1].Value);
        }
        return names;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
