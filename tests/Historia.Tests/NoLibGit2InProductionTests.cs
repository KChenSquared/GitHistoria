namespace Historia.Tests;

/// <summary>
/// Architecture guard: LibGit2Sharp must not appear anywhere in production code.
/// Scans every .cs, .csproj, and .props file under Historia.Core and Historia.App.
/// Comments may reference libgit2 (e.g. trap notes),
/// but code, package references, and using directives must have zero occurrences.
/// </summary>
[Trait("tier", "unit")]
public class NoLibGit2InProductionTests
{
    [Fact]
    public void No_production_project_references_LibGit2Sharp()
    {
        var root = RepoRoot();
        var productionDirs = new[]
        {
            Path.Combine(root, "Historia.Core"),
            Path.Combine(root, "Historia.App"),
        };

        var projectFiles = productionDirs
            .SelectMany(dir => Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".props", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(projectFiles);

        var violations = new List<string>();
        foreach (var file in projectFiles)
        {
            var text = File.ReadAllText(file);
            var stripped = StripXmlComments(text);
            if (stripped.Contains("LibGit2Sharp", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(Path.GetRelativePath(root, file));
            }
        }

        Assert.Empty(violations);
    }

    private static string StripXmlComments(string text) =>
        Regex.Replace(text, @"<!--[\s\S]*?-->", "");

    [Fact]
    public void No_production_source_file_uses_LibGit2Sharp()
    {
        var root = RepoRoot();
        var productionDirs = new[]
        {
            Path.Combine(root, "Historia.Core"),
            Path.Combine(root, "Historia.App"),
        };

        var sourceFiles = productionDirs
            .SelectMany(dir => Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            .ToList();

        Assert.NotEmpty(sourceFiles);

        var violations = new List<string>();
        foreach (var file in sourceFiles)
        {
            var text = File.ReadAllText(file);
            var stripped = StripComments(text);

            if (stripped.Contains("using LibGit2Sharp", StringComparison.Ordinal) ||
                stripped.Contains("LibGit2Sharp.", StringComparison.Ordinal))
            {
                violations.Add(Path.GetRelativePath(root, file));
            }
        }

        Assert.Empty(violations);
    }

    private static string StripComments(string text)
    {
        // Strip block comments /* ... */
        var noBlockComments = Regex.Replace(text, @"/\*[\s\S]*?\*/", m => new string(' ', m.Length));
        // Strip line comments // ...
        var lines = noBlockComments.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        for (var i = 0; i < lines.Length; i++)
        {
            var idx = lines[i].IndexOf("//", StringComparison.Ordinal);
            if (idx >= 0)
                lines[i] = lines[i][..idx];
        }
        return string.Join("\n", lines);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Historia.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
