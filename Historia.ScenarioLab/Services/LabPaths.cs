namespace Historia.ScenarioLab.Services;

/// <summary>
/// Resolves the filesystem root for ScenarioLab data (scripts, templates, instructions, lib)
/// and the surrounding git repository.
/// </summary>
public static class LabPaths
{
    private static readonly string? _resolvedRoot;
    private static readonly string? _repoRoot;

    public static string Root => _resolvedRoot
        ?? throw new InvalidOperationException("Could not locate ScenarioLab root directory.");

    public static string Scripts => Path.Combine(Root, "scripts");
    public static string Templates => Path.Combine(Root, "templates");
    public static string Instructions => Path.Combine(Root, "instructions");
    public static string Lib => Path.Combine(Root, "lib");
    public static string LibScript => Path.Combine(Lib, "scenario.sh");
    public static string RepoRoot => _repoRoot
        ?? Path.GetDirectoryName(Root)
        ?? Root;

    public static bool IsResolved => _resolvedRoot is not null;

    static LabPaths()
    {
        _resolvedRoot = ResolveRoot();
        if (_resolvedRoot is not null)
        {
            _repoRoot = FindRepoRoot(_resolvedRoot) ?? Path.GetDirectoryName(_resolvedRoot);

            try
            {
                Directory.CreateDirectory(Path.Combine(_resolvedRoot, "scripts"));
                Directory.CreateDirectory(Path.Combine(_resolvedRoot, "templates"));
            }
            catch
            {
                // Ignored if permissions restrict; will fail downstream when accessed.
            }
        }
    }

    private static string? ResolveRoot()
    {
        // 1. AssemblyMetadata attribute ("ScenarioLabRoot")
        var asm = typeof(LabPaths).Assembly;
        var meta = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "ScenarioLabRoot", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(meta?.Value) && Directory.Exists(meta.Value))
            return Path.GetFullPath(meta.Value);

        // 2. Fallback: walk up from AppContext.BaseDirectory looking for Historia.ScenarioLab.csproj
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "Historia.ScenarioLab.csproj")))
                return Path.GetFullPath(dir);

            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }

        return null;
    }

    private static string? FindRepoRoot(string start)
    {
        var dir = start;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
                return Path.GetFullPath(dir);

            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }

        return null;
    }
}
