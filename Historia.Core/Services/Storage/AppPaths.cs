namespace Historia.Services.Storage;

/// <summary>
/// Well-known on-disk locations for Historia's persistent state (§V3-9.1).
/// <para>
/// State is split in two. <b>Portable</b> state (settings, profiles, repo bindings, recent
/// repositories) follows the user between machines and resolves to <c>Documents\Historia</c>,
/// falling back to a <c>configs\</c> folder beside the executable. <b>Machine</b> state (window
/// geometry, the session file, logs) is machine-specific and high-churn and stays in
/// <c>%APPDATA%\Historia\</c> with no resolution and no probe.
/// </para>
/// </summary>
public static class AppPaths
{
    private static string? _override;
    private static string? _cachedPortable;
    private static readonly object _sync = new();

    /// <summary>Redirects <b>all</b> persistent state to <paramref name="directory"/> (test seam,
    /// §V3-9.1). Short-circuits the whole resolution and invalidates the cache.</summary>
    public static void OverrideForTesting(string? directory)
    {
        lock (_sync)
        {
            _override = directory;
            _cachedPortable = null;
        }
    }

    /// <summary>Portable state — resolved once and cached for the process lifetime (§V3-9.1 Trap).</summary>
    public static string DataDirectory
    {
        get
        {
            lock (_sync)
            {
                if (_override is not null)
                    return _override;
                return _cachedPortable ??= ResolveFor(
                    () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    DefaultProbe);
            }
        }
    }

    /// <summary>
    /// Machine-specific state. Never redirected to a network share, never synced. No candidates,
    /// no probe — <c>%APPDATA%</c> is writable on any machine that can run the app.
    /// </summary>
    public static string MachineDirectory =>
        _override ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Historia");

    /// <summary>True when neither portable candidate was writable, so persistence is disabled for
    /// the session (rule 0.4). The last <see cref="ResolveFor"/> call sets this.</summary>
    public static bool PortableResolutionFailed { get; private set; }

    /// <summary>The two paths the resolution tried, for the rule 0.4 warning.</summary>
    public static (string Documents, string Configs) AttemptedPortablePaths { get; private set; }

    /// <summary>
    /// Portable resolution with injectable seams so <c>AppPathsTests</c> can force a probe
    /// failure without an unwritable filesystem (§V3-9.1 Trap).
    /// </summary>
    internal static string ResolveFor(Func<string> documents, Func<string, bool> probe)
    {
        var docsRoot = SafeInvoke(documents);
        var docs = string.IsNullOrEmpty(docsRoot) ? "" : Path.Combine(docsRoot, "Historia");
        var configs = Path.Combine(AppContext.BaseDirectory, "configs");
        AttemptedPortablePaths = (docs, configs);

        if (!string.IsNullOrEmpty(docs) && probe(docs))
        {
            PortableResolutionFailed = false;
            return docs;
        }

        if (probe(configs))
        {
            PortableResolutionFailed = false;
            return configs;
        }

        // Rule 0.4: both failed — do not silently redirect into MachineDirectory. Report and
        // run without persistence. Return the configs path as an in-memory placeholder so
        // callers have a non-null path; writes there will simply fail.
        PortableResolutionFailed = true;
        return configs;
    }

    /// <summary>Create the directory, write and delete a probe file. Any exception disqualifies it.</summary>
    private static bool DefaultProbe(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".Historia-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SafeInvoke(Func<string> f)
    {
        try { return f() ?? ""; }
        catch { return ""; }
    }

    // ── Portable files ──
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string ProfilesFile => Path.Combine(DataDirectory, "profiles.json");
    public static string RepoBindingsFile => Path.Combine(DataDirectory, "repo-profiles.json");
    public static string RepoLayoutFile => Path.Combine(DataDirectory, "repo-columns.json");
    public static string RecentRepositoriesFile => Path.Combine(DataDirectory, "recent-repositories.json");

    /// <summary>§V6-G9: one JSON file per user-authored theme lives here.</summary>
    public static string ThemesDirectory => Path.Combine(DataDirectory, "themes");

    /// <summary>v8 M11: SSH keys Historia generates for a profile live under a per-profile
    /// subfolder here (<c>keys/&lt;profileId&gt;/id_ed25519</c>).</summary>
    public static string KeysDirectory => Path.Combine(DataDirectory, "keys");

    /// <summary>§V6-G9: the file backing the custom theme with the given id (its file stem).</summary>
    public static string CustomThemeFile(string id) => Path.Combine(ThemesDirectory, id + ".json");

    // ── Machine files ──
    public static string LogsDirectory => Path.Combine(MachineDirectory, "logs");

    /// <summary>Regenerable machine-local caches (e.g. the persisted graph walk). Safe to wipe.</summary>
    public static string CacheDirectory => Path.Combine(MachineDirectory, "cache");
    public static string WindowPlacementFile => Path.Combine(MachineDirectory, "window.json");
    public static string SessionFile => Path.Combine(MachineDirectory, "session.json");

    /// <summary>The portable files, for the one-time copy migration (§V3-9.1).</summary>
    internal static readonly string[] PortableFileNames =
        ["settings.json", "profiles.json", "repo-profiles.json", "repo-columns.json", "recent-repositories.json"];

    public static void EnsureCreated()
    {
        try { Directory.CreateDirectory(DataDirectory); } catch { /* rule 0.4 */ }
        Directory.CreateDirectory(MachineDirectory);
        Directory.CreateDirectory(LogsDirectory);
        try { Directory.CreateDirectory(CacheDirectory); } catch { /* regenerable */ }
        try { Directory.CreateDirectory(ThemesDirectory); } catch { /* rule 0.4 */ }
        MigratePortableFilesFromAppData();
    }

    /// <summary>
    /// Developer tool (§21): deletes <b>everything</b> under both state roots — settings,
    /// profiles, repo bindings, recent list, themes, the session file, window geometry, every
    /// cache file and every log. Best-effort: a file held open (an active log, a cache handle)
    /// is skipped and gone on the next launch. The root folders themselves are kept so the
    /// running app does not trip over a missing directory; <see cref="EnsureCreated"/> repopulates
    /// them. Returns the number of files removed. Irreversible.
    /// </summary>
    public static int WipeAllLocalData()
    {
        var roots = new[] { DataDirectory, MachineDirectory }
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var removed = 0;
        foreach (var root in roots)
        {
            DirectoryInfo di;
            try { di = new DirectoryInfo(root); if (!di.Exists) continue; }
            catch { continue; }

            foreach (var f in SafeEnumerate(() => di.GetFiles("*", SearchOption.AllDirectories)))
            {
                try { f.Attributes = FileAttributes.Normal; f.Delete(); removed++; }
                catch { /* locked — cleared on next launch */ }
            }
            foreach (var sub in SafeEnumerate(() => di.GetDirectories()))
            {
                try { sub.Delete(recursive: true); } catch { /* left holding a locked file */ }
            }
        }
        return removed;

        static T[] SafeEnumerate<T>(Func<T[]> get)
        {
            try { return get(); } catch { return []; }
        }
    }

    /// <summary>
    /// One-time copy of the portable set from a prior <c>%APPDATA%\Historia\</c> install into the
    /// chosen portable directory (§V3-9.1). Copy, never move — a downgrade still finds its state.
    /// <c>session.json</c> and <c>logs/</c> are machine state and are already in their final home.
    /// </summary>
    private static void MigratePortableFilesFromAppData()
    {
        if (_override is not null || PortableResolutionFailed)
            return;

        var portable = DataDirectory;
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Historia");
        if (string.Equals(Path.GetFullPath(portable), Path.GetFullPath(appData), StringComparison.OrdinalIgnoreCase))
            return;
        if (File.Exists(Path.Combine(portable, "settings.json")) || !File.Exists(Path.Combine(appData, "settings.json")))
            return;

        try
        {
            foreach (var name in PortableFileNames)
            {
                var src = Path.Combine(appData, name);
                var dst = Path.Combine(portable, name);
                if (File.Exists(src) && !File.Exists(dst))
                    File.Copy(src, dst);
            }

            // §V6-G9: the themes/ folder is portable state too — copied wholesale rather than
            // added to PortableFileNames (that list is the fixed one-time file set).
            var themesSrc = Path.Combine(appData, "themes");
            var themesDst = Path.Combine(portable, "themes");
            if (Directory.Exists(themesSrc) && !Directory.Exists(themesDst))
            {
                Directory.CreateDirectory(themesDst);
                foreach (var f in Directory.EnumerateFiles(themesSrc, "*.json"))
                    File.Copy(f, Path.Combine(themesDst, Path.GetFileName(f)), overwrite: false);
            }
        }
        catch
        {
            // Non-fatal: the app starts with defaults if the copy fails.
        }
    }
}
