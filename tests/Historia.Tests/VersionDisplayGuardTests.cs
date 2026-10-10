namespace Historia.Tests;

[Trait("tier", "unit")]
public class VersionDisplayGuardTests
{
    // Allowlist: FrameBenchRunner reads Avalonia's assembly version for benchmark diagnostics.
    private static readonly HashSet<string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "FrameBenchRunner.cs",
    };

    [Fact]
    public void NoAssemblyGetNameVersionInShippedCode()
    {
        var root = RepoRoot();
        var targetProjects = new[] { "Historia.App", "Historia.Core", "Historia.Cmdr" };
        var offenders = new List<string>();

        foreach (var project in targetProjects)
        {
            var projectDir = Path.Combine(root, project);
            if (!Directory.Exists(projectDir))
                continue;

            foreach (var file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
            {
                var normalized = file.Replace('\\', '/');
                if (normalized.Contains("/bin/") || normalized.Contains("/obj/"))
                    continue;

                var fileName = Path.GetFileName(file);
                if (AllowedFiles.Contains(fileName))
                    continue;

                var text = File.ReadAllText(file);
                if (text.Contains("GetName().Version"))
                {
                    var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                    offenders.Add(rel);
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Assembly.GetName().Version found in shipped code; use AppVersion.Current for display version:\n  " +
            string.Join("\n  ", offenders));
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
