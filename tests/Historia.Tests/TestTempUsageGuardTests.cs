namespace Historia.Tests;

/// <summary>
/// Guard against tests creating fixtures in the operating system temp directory.
/// All test fixtures must be created under the project scratch root via TestScratch.
/// Allowed uses of temp paths are restricted to an explicit allowlist where nothing is
/// created (path arithmetic or external working directories).
/// </summary>
[Trait("tier", "unit")]
public class TestTempUsageGuardTests
{
    private static readonly Dictionary<string, string> TempAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TestScratch.cs"] = "Fallback scratch root when assembly metadata is missing.",
        ["TestScratchTests.cs"] = "Exercises fallback scratch root and prune logic.",
        ["ControlInputValidationTests.cs"] = "Path arithmetic only; no files created.",
        ["ControlArgsTests.cs"] = "Path arithmetic only; no files created.",
        ["GitCliServiceTests.cs"] = "External working directory outside git work trees; no files created.",
        ["CommitHeaderReaderTests.cs"] = "External working directory outside git work trees; no files created.",
        ["GraphClonePrecacheTests.cs"] = "External working directory outside git work trees; no files created.",
        ["RepositoryDiscoveryTests.cs"] = "Discovery root search; no files created.",
    };

    private static readonly Regex TempPattern =
        new(@"GetTemp(?:Path|FileName|Directory)\s*\(", RegexOptions.Compiled);

    private static readonly Regex DirectoryDeletePattern =
        new(@"Directory\.Delete\s*\(", RegexOptions.Compiled);

    [Fact]
    public void Tests_do_not_create_files_in_the_system_temp_folder()
    {
        var root = RepoRoot();
        var testsDir = Path.Combine(root, "tests");
        var violations = new List<string>();

        foreach (var file in EnumerateTestFiles(testsDir))
        {
            var fileName = Path.GetFileName(file);
            if (string.Equals(fileName, "TestTempUsageGuardTests.cs", StringComparison.OrdinalIgnoreCase))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var stripped = StripLineComment(line);

                if (TempPattern.IsMatch(stripped) && !TempAllowlist.ContainsKey(fileName))
                {
                    var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                    violations.Add($"{rel}:{i + 1}: {line.Trim()}");
                }

                if (DirectoryDeletePattern.IsMatch(stripped) && stripped.Contains("GetTemp", StringComparison.OrdinalIgnoreCase))
                {
                    var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                    violations.Add($"{rel}:{i + 1}: Directory.Delete on temp path: {line.Trim()}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Direct temporary directory usage detected in tests. Use TestScratch instead:\n  " +
            string.Join("\n  ", violations));
    }

    [Fact]
    public void Temp_allowlist_has_no_stale_entries()
    {
        var root = RepoRoot();
        var testsDir = Path.Combine(root, "tests");
        var matchedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in EnumerateTestFiles(testsDir))
        {
            var fileName = Path.GetFileName(file);
            if (string.Equals(fileName, "TestTempUsageGuardTests.cs", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = StripComments(File.ReadAllText(file));
            if (TempPattern.IsMatch(text) && TempAllowlist.ContainsKey(fileName))
            {
                matchedFiles.Add(fileName);
            }
        }

        var staleEntries = TempAllowlist.Keys
            .Where(k => !matchedFiles.Contains(k))
            .ToList();

        Assert.True(staleEntries.Count == 0,
            "Temp allowlist contains entries with no matching GetTemp calls:\n  " +
            string.Join("\n  ", staleEntries));
    }

    private static IEnumerable<string> EnumerateTestFiles(string testsDir)
    {
        foreach (var file in Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(testsDir, file).Replace('\\', '/');
            if (rel.Contains("/bin/") || rel.Contains("/obj/") ||
                rel.StartsWith("bin/") || rel.StartsWith("obj/"))
                continue;

            yield return file;
        }
    }

    private static string StripLineComment(string line)
    {
        var idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
    }

    private static string StripComments(string text)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"(?<!:)//[^\n]*", " ");
        return text;
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
