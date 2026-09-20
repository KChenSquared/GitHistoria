namespace Historia.Tests;

/// <summary>
/// Guard against write-only (inert) settings. A setting defined on <see cref="AppSettings"/>
/// that has no readers outside of its definition and settings editor is dead UI.
/// This test scans the source files of Historia.Core, Historia.App, and Historia.ScenarioLab,
/// counting references to each public property outside of AppSettings.cs and the settings views.
///
/// Note: This test verifies reachability (the existence of at least one reader in code),
/// not behavioural correctness of the reader.
/// </summary>
[Trait("tier", "unit")]
public class SettingsReachabilityTests
{
    /// <summary>
    /// Deliberately persisted settings that have no reader in code.
    /// This allowlist must remain empty unless explicitly approved.
    /// </summary>
    private static readonly HashSet<string> Allowlist =
    [
        // §13.5: Gates hosting-service API polling when a token-bearing profile is active.
        // Its original reader in NetworkPolicyService was removed when per-repo routing replaced
        // global network modes (v9), and the hosting-service layer (PRs, issue metadata, §23)
        // is not yet implemented.
        nameof(AppSettings.EnableBackgroundPolling),
    ];

    [Fact]
    public void All_app_settings_have_at_least_one_reader()
    {
        var root = RepoRoot();
        var files = SourceFiles(root).ToList();
        var fileTexts = files.Select(f => StripComments(File.ReadAllText(f))).ToList();

        var properties = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var unread = new List<string>();

        foreach (var prop in properties)
        {
            if (Allowlist.Contains(prop.Name))
                continue;

            var pattern = new Regex($@"\b{Regex.Escape(prop.Name)}\b", RegexOptions.Compiled);
            var count = fileTexts.Count(t => pattern.IsMatch(t));

            if (count == 0)
                unread.Add(prop.Name);
        }

        Assert.True(unread.Count == 0,
            $"The following AppSettings properties have zero readers outside AppSettings.cs and SettingsView.cs:\n  "
            + string.Join("\n  ", unread)
            + "\nWire each setting to its consumer or delete it if unused.");
    }

    private static IEnumerable<string> SourceFiles(string root)
    {
        var dirs = new[] { "Historia.Core", "Historia.App", "Historia.ScenarioLab" };
        foreach (var dir in dirs)
        {
            var fullDir = Path.Combine(root, dir);
            if (!Directory.Exists(fullDir))
                continue;

            foreach (var file in Directory.EnumerateFiles(fullDir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.StartsWith("bin/") || rel.StartsWith("obj/"))
                    continue;

                var fileName = Path.GetFileName(file);
                if (fileName.Equals("AppSettings.cs", StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals("SettingsView.cs", StringComparison.OrdinalIgnoreCase)
                    || fileName.StartsWith("RepoSettingsView", StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return file;
            }
        }
    }

    private static string StripComments(string text)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"//[^\n]*", " ");
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
