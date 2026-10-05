namespace Historia.Tests;

/// <summary>
/// Guard against write-only (inert) settings. A setting defined on <see cref="AppSettings"/>
/// that has no readers outside of its definition and settings editor is dead UI.
/// This test scans the source files of Historia.Core, Historia.App, and Historia.ScenarioLab,
/// outside of AppSettings.cs and the settings views, for a member-access read of each public
/// property (<c>.Name</c>).
///
/// Trap: matching the bare name is not enough. A same-named constant or field, an enum type of
/// the same name, a <c>with { Name = … }</c> initialiser (a write) and
/// <c>nameof(AppSettings.Name)</c> all satisfy a name-only match while the setting itself has no
/// reader — that is how unread settings slipped past this guard. Comments and <c>nameof(…)</c>
/// are stripped before matching.
///
/// Note: This test verifies reachability (the existence of at least one reader in code),
/// not behavioural correctness of the reader.
/// </summary>
[Trait("tier", "unit")]
public class SettingsReachabilityTests
{
    /// <summary>
    /// Deliberately persisted settings that have no member-access reader in code.
    /// This allowlist must stay limited to explicitly approved entries.
    /// </summary>
    private static readonly HashSet<string> Allowlist =
    [
        // A persistence stamp, not a user setting: the migration pipeline reads it from the JSON
        // document ("schemaVersion"), and the service only ever writes it.
        nameof(AppSettings.SchemaVersion),
    ];

    [Fact]
    public void All_app_settings_have_at_least_one_reader()
    {
        var fileTexts = StrippedSources();

        var unread = SettingNames()
            .Where(name => !Allowlist.Contains(name))
            .Where(name => !fileTexts.Any(t => IsRead(t, name)))
            .ToList();

        Assert.True(unread.Count == 0,
            $"The following AppSettings properties have no member-access reader outside AppSettings.cs and the settings views:\n  "
            + string.Join("\n  ", unread)
            + "\nWire each setting to its consumer or delete it if unused.");
    }

    [Fact]
    public void Allowlisted_settings_are_still_unread()
    {
        var fileTexts = StrippedSources();
        var names = SettingNames().ToHashSet();

        foreach (var name in Allowlist)
        {
            Assert.True(names.Contains(name), $"Allowlisted '{name}' is not an AppSettings property; remove it from the allowlist.");
            Assert.False(fileTexts.Any(t => IsRead(t, name)),
                $"Allowlisted '{name}' now has a member-access reader; remove it from the allowlist.");
        }
    }

    [Theory]
    [InlineData("private const double TabWidth = 186;", "TabWidth", false)]
    [InlineData("public enum TerminalBellStyle { None, Visual }", "TerminalBellStyle", false)]
    [InlineData("_ = _cfg.UpdateAsync(s => s with { TabWidth = 4 });", "TabWidth", false)]
    [InlineData("var n = nameof(AppSettings.TabWidth);", "TabWidth", false)]
    [InlineData("// reads _settings.Current.TabWidth", "TabWidth", false)]
    [InlineData("var w = _settings.Current.TabWidth;", "TabWidth", true)]
    [InlineData("if (e.NewSettings.TabWidth != 4) { }", "TabWidth", true)]
    [InlineData("var w = settings .TabWidth;", "TabWidth", true)]
    public void IsRead_ignores_same_named_identifiers(string source, string property, bool expected)
    {
        Assert.Equal(expected, IsRead(Strip(source), property));
    }

    /// <summary>True when <paramref name="strippedSource"/> (already passed through
    /// <see cref="Strip"/>) reads <paramref name="property"/> by member access.</summary>
    internal static bool IsRead(string strippedSource, string property)
        => Regex.IsMatch(strippedSource, $@"\.\s*{Regex.Escape(property)}\b");

    /// <summary>Removes comments and <c>nameof(…)</c> expressions, neither of which reads a value.</summary>
    internal static string Strip(string text)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"//[^\n]*", " ");
        text = Regex.Replace(text, @"\bnameof\s*\([^()]*\)", " ");
        return text;
    }

    private static IEnumerable<string> SettingNames()
        => typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name);

    private static List<string> StrippedSources()
        => SourceFiles(RepoRoot()).Select(f => Strip(File.ReadAllText(f))).ToList();

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

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
