namespace Historia.Tests;

/// <summary>
/// Scratch root for test runs. Honours <c>HISTORIA_TEST_TMP</c> so the scratch
/// directory can be pinned to a fixed path and excluded from real-time virus scans.
/// Every directory a run creates lives under one <c>historia-run-&lt;pid&gt;-&lt;guid&gt;</c> folder,
/// deleted when the process exits; a run that never got that far is swept by the next one.
/// </summary>
public static class TestScratch
{
    private const string RunPrefix = "historia-run-";

    public static string Root
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("HISTORIA_TEST_TMP");
            return !string.IsNullOrWhiteSpace(env) ? env : Path.GetTempPath();
        }
    }

    private static readonly ConcurrentDictionary<string, string> RunRoots = new(StringComparer.OrdinalIgnoreCase);

    // In the background: deleting a previous run's leftovers used to block the first test on every
    // worker thread for as long as the delete took.
    private static readonly Lazy<Task> PruneOnce = new(() => Task.Run(PruneStale));

    private static readonly Lazy<bool> ExitHook = new(() =>
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var runRoot in RunRoots.Values)
                TryDelete(new DirectoryInfo(runRoot));
        };
        return true;
    });

    public static string NewDirectory(string prefix = "historia-test-")
    {
        _ = PruneOnce.Value;
        _ = ExitHook.Value;
        var runRoot = RunRoots.GetOrAdd(Root, root =>
        {
            var dir = Path.Combine(root, $"{RunPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        });
        var path = Path.Combine(runRoot, prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Deletes the run folders of test processes that are no longer running, and any test
    /// directory older than 24 hours under the scratch root. Safe against concurrent test
    /// processes since it never touches a live run's folder or a fresh directory.
    /// </summary>
    public static void PruneStale()
    {
        try
        {
            var root = Root;
            if (!Directory.Exists(root))
                return;

            var cutoff = DateTime.UtcNow.AddHours(-24);
            foreach (var dir in Directory.EnumerateDirectories(root, RunPrefix + "*"))
            {
                var info = new DirectoryInfo(dir);
                if (RunRoots.Values.Contains(dir, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (!IsRunAlive(info.Name) || info.LastWriteTimeUtc < cutoff)
                    TryDelete(info);
            }

            // Directories from before the per-run folder existed.
            var patterns = new[] { "historia-tpl-*", "historia-test-*", "historia-state-*" };
            foreach (var pattern in patterns)
            {
                foreach (var dir in Directory.EnumerateDirectories(root, pattern))
                {
                    var info = new DirectoryInfo(dir);
                    if (info.LastWriteTimeUtc < cutoff)
                        TryDelete(info);
                }
            }
        }
        catch
        {
            // best effort
        }
    }

    private static bool IsRunAlive(string runFolderName)
    {
        var rest = runFolderName[RunPrefix.Length..];
        var dash = rest.IndexOf('-');
        if (dash <= 0 || !int.TryParse(rest[..dash], out var pid))
            return false;
        if (pid == Environment.ProcessId)
            return true;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(DirectoryInfo dir)
    {
        try
        {
            if (!dir.Exists)
                return;
            ClearReadOnly(dir);
            dir.Delete(recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private static void ClearReadOnly(DirectoryInfo dir)
    {
        try
        {
            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                    file.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
        catch { }
    }
}
