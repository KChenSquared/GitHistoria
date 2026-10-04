namespace Historia.Tests;

/// <summary>
/// Render paths draw cached, shaped <c>TextLayout</c>s and never draw <c>FormattedText</c>:
/// a cached <c>FormattedText</c> re-shapes its text on every draw, so caching it saves the
/// allocation but not the shaping, and a miss counter on such a cache stays at zero while every
/// visible string is shaped every frame. Nothing fails when a <c>DrawText</c> call creeps back
/// in, only frames get slower, so this scans the render-path sources for one.
/// </summary>
public sealed class ShapedTextRenderPathTests
{
    /// <summary>The render-path sources, relative to the repository root.</summary>
    private static readonly string[] RenderPathFiles =
    [
        "Historia.App/Controls/CommitGraphControl.cs",
        "Historia.App/Controls/Diff/DiffRenderers.cs",
        "Historia.App/Controls/Diff/DiffMinimap.cs",
        "Historia.App/Controls/Conflicts/ConflictPanes.cs",
    ];

    /// <summary>
    /// Intentional exceptions, as "relative/path.cs: exact trimmed source line", each with a
    /// comment saying why that draw is not per frame. Empty: no render-path file draws
    /// <c>FormattedText</c>.
    /// </summary>
    private static readonly HashSet<string> Allowlist = new(StringComparer.Ordinal)
    {
    };

    [Fact]
    public void Render_path_files_never_call_DrawText()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var rel in RenderPathFiles)
        {
            var path = Path.Combine(root, rel);
            Assert.True(File.Exists(path), $"render-path file moved or renamed: {rel}");

            foreach (var line in StripComments(File.ReadAllText(path)).Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Contains(".DrawText(", StringComparison.Ordinal)
                    && !Allowlist.Contains($"{rel}: {trimmed}"))
                {
                    offenders.Add($"{rel}: {trimmed}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "a cached FormattedText re-shapes on every draw; draw a cached TextLayout (ShapedTextCache) instead:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Render_path_files_keep_no_FormattedText_beyond_measurement_probes()
    {
        // A FormattedText field, cache or return type in a render-path file is a FormattedText
        // waiting to be drawn. Constructing one inline to measure a width is fine.
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var rel in RenderPathFiles)
        {
            var source = StripComments(File.ReadAllText(Path.Combine(root, rel)));
            foreach (var line in source.Split('\n'))
            {
                var trimmed = line.Trim();
                if (Regex.IsMatch(trimmed, @"(?<!new )\bFormattedText\b"))
                    offenders.Add($"{rel}: {trimmed}");
            }
        }

        Assert.True(offenders.Count == 0,
            "only inline `new FormattedText(...)` measurement probes may appear in render-path files:\n  "
            + string.Join("\n  ", offenders));
    }

    private static string StripComments(string text)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(text, @"(?<!:)//[^\n]*", " ");
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
