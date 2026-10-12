namespace Historia.Tests;

/// <summary>
/// every name referenced in native .cs resolves in <see cref="IconPaths"/>,
/// and no <see cref="IconPaths"/> entry is dead. Glyphs are Google Material Symbols on a 960-unit grid.
/// </summary>
[Trait("tier", "unit")]
public class IconPathTests
{
    // Glyphs reserved until remaining views or affordances consume them.
    private static readonly HashSet<string> ReservedForLaterMilestones =
        new(StringComparer.Ordinal);

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
    public void GlyphsSitOnTheMaterialGrid()
    {
        foreach (var name in IconPaths.Names)
        {
            var d = IconPaths.Get(name);
            var (minX, maxX, minY, maxY) = GetPathBounds(d);
            Assert.True(minX >= 0, $"'{name}' minX {minX} is < 0");
            Assert.True(maxX <= 960, $"'{name}' maxX {maxX} is > 960");
            Assert.True(minY >= -960, $"'{name}' minY {minY} is < -960");
            Assert.True(maxY <= 0, $"'{name}' maxY {maxY} is > 0");
        }
    }

    [Fact]
    public void VendoredSvgsMatchGeneratedFile()
    {
        var root = RepoRoot();
        var svgDir = Path.Combine(root, "assets", "material-symbols");
        Assert.True(Directory.Exists(svgDir), "assets/material-symbols directory does not exist.");
        var fileNames = Directory.EnumerateFiles(svgDir, "*.svg")
            .Select(f => Path.GetFileNameWithoutExtension(f)!)
            .ToHashSet(StringComparer.Ordinal);
        var iconNames = IconPaths.Names.ToHashSet(StringComparer.Ordinal);
        Assert.Equal(fileNames, iconNames);
    }

    [Fact]
    public void NoLucideReferencesRemain()
    {
        var root = RepoRoot();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".axaml", ".ps1", ".md" };
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            if (!extensions.Contains(ext))
                continue;

            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.StartsWith("Historia-DevDocs/", StringComparison.OrdinalIgnoreCase) ||
                rel.StartsWith(".claude/", StringComparison.OrdinalIgnoreCase) ||
                rel.Equals("tests/Historia.Tests/IconPathTests.cs", StringComparison.OrdinalIgnoreCase) ||
                rel.Contains("/bin/") || rel.Contains("/obj/") ||
                rel.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
                rel.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
                rel.Contains("/.git/") || rel.StartsWith(".git/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (text.Contains("lucide", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(rel);
            }
        }

        Assert.True(violations.Count == 0,
            "Lucide references found in: " + string.Join(", ", violations));
    }

    private static (double MinX, double MaxX, double MinY, double MaxY) GetPathBounds(string d)
    {
        var tokens = Regex.Matches(d, @"([a-zA-Z])|([-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)");
        double curX = 0, curY = 0;
        double startX = 0, startY = 0;
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;

        void Track(double x, double y)
        {
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        char cmd = ' ';
        int i = 0;
        bool isFirst = true;

        bool HasNum() => i < tokens.Count && !char.IsLetter(tokens[i].Value[0]);

        double NextNum()
        {
            if (HasNum())
                return double.Parse(tokens[i++].Value, System.Globalization.CultureInfo.InvariantCulture);
            return 0;
        }

        while (i < tokens.Count)
        {
            var tok = tokens[i].Value;
            if (char.IsLetter(tok[0]))
            {
                cmd = tok[0];
                i++;
            }

            switch (cmd)
            {
                case 'M':
                    while (HasNum())
                    {
                        curX = NextNum();
                        curY = NextNum();
                        startX = curX; startY = curY;
                        Track(curX, curY);
                    }
                    break;
                case 'm':
                    // Only the first pair is a moveto and starts a subpath (a later Z returns to
                    // it); the rest are implicit relative linetos. The very first moveto is absolute.
                    var movedTo = false;
                    while (HasNum())
                    {
                        if (isFirst)
                        {
                            curX = NextNum();
                            curY = NextNum();
                            isFirst = false;
                        }
                        else
                        {
                            curX += NextNum();
                            curY += NextNum();
                        }
                        if (!movedTo)
                        {
                            startX = curX; startY = curY;
                            movedTo = true;
                        }
                        Track(curX, curY);
                    }
                    break;
                case 'L':
                    while (HasNum())
                    {
                        curX = NextNum();
                        curY = NextNum();
                        Track(curX, curY);
                    }
                    break;
                case 'l':
                    while (HasNum())
                    {
                        curX += NextNum();
                        curY += NextNum();
                        Track(curX, curY);
                    }
                    break;
                case 'H':
                    while (HasNum())
                    {
                        curX = NextNum();
                        Track(curX, curY);
                    }
                    break;
                case 'h':
                    while (HasNum())
                    {
                        curX += NextNum();
                        Track(curX, curY);
                    }
                    break;
                case 'V':
                    while (HasNum())
                    {
                        curY = NextNum();
                        Track(curX, curY);
                    }
                    break;
                case 'v':
                    while (HasNum())
                    {
                        curY += NextNum();
                        Track(curX, curY);
                    }
                    break;
                case 'Q':
                    while (HasNum())
                    {
                        double cx = NextNum(), cy = NextNum();
                        Track(cx, cy);
                        curX = NextNum(); curY = NextNum();
                        Track(curX, curY);
                    }
                    break;
                case 'q':
                    while (HasNum())
                    {
                        double cx = curX + NextNum(), cy = curY + NextNum();
                        Track(cx, cy);
                        curX += NextNum(); curY += NextNum();
                        Track(curX, curY);
                    }
                    break;
                case 'T':
                    while (HasNum())
                    {
                        curX = NextNum(); curY = NextNum();
                        Track(curX, curY);
                    }
                    break;
                case 't':
                    while (HasNum())
                    {
                        curX += NextNum(); curY += NextNum();
                        Track(curX, curY);
                    }
                    break;
                case 'Z':
                case 'z':
                    curX = startX; curY = startY;
                    break;
            }
            isFirst = false;
        }

        return (minX, maxX, minY, maxY);
    }

    private static HashSet<string> ReferencedIconNames()
    {
        var root = RepoRoot();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var rxRazor = new Regex("<Icon\\s+[^>]*?Name=\"([a-z0-9_]+)\"", RegexOptions.IgnoreCase);
        var rxAxaml = new Regex("IconGlyph\\s+[^>]*?\\bIcon=\"([a-z0-9_]+)\"");
        var rxCs = new Regex("(?:IconPaths\\.Get|\\bIcon(?:Button|GlyphPath|TextButton)?|\\bArrowButton)\\(\"([a-z0-9_]+)\"");

        foreach (var pattern in new[] { "*.razor", "*.cs", "*.axaml" })
            foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Contains("/bin/") || rel.Contains("/obj/")
                    || rel.Contains("/tests/") || rel.StartsWith("tests/", StringComparison.Ordinal)
                    || rel.StartsWith(".claude/", StringComparison.OrdinalIgnoreCase))
                    continue;
                var text = File.ReadAllText(file);
                var rx = file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ? rxRazor
                    : file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) ? rxAxaml : rxCs;
                foreach (Match m in rx.Matches(text))
                    names.Add(m.Groups[1].Value);
            }
        return names;
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
