namespace Historia.ScenarioLab.Services;

/// <summary>
/// Machine-specific paths for scenario execution scratch locations.
/// </summary>
public sealed record LabSettings(string Local1 = "", string Local2 = "", string Remotes = "")
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Local1) &&
        !string.IsNullOrWhiteSpace(Local2) &&
        !string.IsNullOrWhiteSpace(Remotes);

    public Dictionary<string, string> Validate(string repoRoot)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        ValidateField("Local1", Local1, "Local 1", repoRoot, errors);
        ValidateField("Local2", Local2, "Local 2", repoRoot, errors);
        ValidateField("Remotes", Remotes, "Remotes", repoRoot, errors);

        // Check mutual conflicts only if individual paths are valid
        var paths = new List<(string Key, string Label, string FullPath)>();
        foreach (var (key, label, val) in new[] { ("Local1", "Local 1", Local1), ("Local2", "Local 2", Local2), ("Remotes", "Remotes", Remotes) })
        {
            if (!errors.ContainsKey(key) && !string.IsNullOrWhiteSpace(val))
            {
                try
                {
                    var full = Path.GetFullPath(val).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    paths.Add((key, label, full));
                }
                catch
                {
                }
            }
        }

        for (int i = 0; i < paths.Count; i++)
        {
            for (int j = i + 1; j < paths.Count; j++)
            {
                var a = paths[i];
                var b = paths[j];

                if (string.Equals(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase))
                {
                    errors[a.Key] = $"{a.Label} cannot be the same directory as {b.Label}.";
                    errors[b.Key] = $"{b.Label} cannot be the same directory as {a.Label}.";
                }
                else if (a.FullPath.StartsWith(b.FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    errors[a.Key] = $"{a.Label} cannot be inside {b.Label}.";
                }
                else if (b.FullPath.StartsWith(a.FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    errors[b.Key] = $"{b.Label} cannot be inside {a.Label}.";
                }
            }
        }

        return errors;
    }

    private static void ValidateField(string key, string path, string label, string repoRoot, Dictionary<string, string> errors)
    {
        var error = FolderPurger.CheckPathSafety(path, label, repoRoot);
        if (error is not null)
        {
            errors[key] = error;
        }
    }
}

/// <summary>
/// JSON persistence for LabSettings at %APPDATA%\Historia.ScenarioLab\settings.json.
/// </summary>
public static class LabSettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Historia.ScenarioLab");

    private static readonly string SettingsFile = Path.Combine(SettingsDirectory, "settings.json");

    public static LabSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                var settings = JsonSerializer.Deserialize<LabSettings>(json);
                if (settings is not null)
                    return settings;
            }
        }
        catch
        {
            // Fall back to empty settings on read or parse failure.
        }

        return new LabSettings();
    }

    public static void Save(LabSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);

        var tempFile = Path.Combine(SettingsDirectory, $"settings.{Guid.NewGuid():N}.tmp");
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(tempFile, json, Encoding.UTF8);
        File.Move(tempFile, SettingsFile, overwrite: true);
    }
}
