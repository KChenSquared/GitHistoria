namespace Historia.Tests;

/// <summary>
/// Scratch root for test runs. Fixtures live under a scratch root inside each test project's
/// build output directory, isolating them from the operating system's temporary directory.
/// Each test process creates a per-run folder deleted on exit, and run folders of finished
/// processes older than 30 minutes are purged on first use.
/// </summary>
public static class TestScratch
{
    private const string RunPrefix = "historia-run-";

    private static readonly TimeSpan StaleAge = TimeSpan.FromMinutes(30);

    public static string Root => GetRoot(typeof(TestScratch).Assembly);

    internal static string GetRoot(Assembly? assembly = null)
    {
        var env = Environment.GetEnvironmentVariable("HISTORIA_TEST_TMP");
        string root;
        if (!string.IsNullOrWhiteSpace(env))
        {
            root = env;
        }
        else if (assembly != null)
        {
            var meta = assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => string.Equals(a.Key, "TestScratchRoot", StringComparison.OrdinalIgnoreCase));
            root = !string.IsNullOrWhiteSpace(meta?.Value) ? meta.Value : Path.GetTempPath();
        }
        else
        {
            root = Path.GetTempPath();
        }

        try
        {
            root = Path.GetFullPath(root);
            Directory.CreateDirectory(root);
        }
        catch
        {
            // Best effort directory creation.
        }

        return root;
    }

    private static readonly ConcurrentDictionary<string, string> RunRoots = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<Task> PruneOnce = new(() => Task.Run(PruneStale));

    // The test host is killed moments after exit begins, too soon to delete a large run folder
    // in-process, so on Windows a separate rd process removes it and outlives the host.
    private static readonly Lazy<bool> ExitHook = new(() =>
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var runRoot in RunRoots.Values)
            {
                if (!TryDeleteDetached(runRoot))
                    Delete(runRoot);
            }
        };
        return true;
    });

    /// <summary>
    /// Starts <c>rd /s /q</c> on a run folder. It deletes read-only files and removes junctions
    /// without following them. Returns false when the folder cannot be handed off safely.
    /// </summary>
    private static bool TryDeleteDetached(string runRoot)
    {
        if (!OperatingSystem.IsWindows()
            || !Path.GetFileName(runRoot).StartsWith(RunPrefix, StringComparison.Ordinal)
            || runRoot.IndexOfAny(['"', '%']) >= 0)
            return false;

        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = $"/d /c \"rd /s /q \"{runRoot}\" >nul 2>&1\"",
            });
            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="directory"/> ignores letter case: a file written as
    /// <c>probe</c> can be opened as <c>PROBE</c>.
    /// </summary>
    public static bool IsCaseInsensitive(string directory)
    {
        var probe = Path.Combine(directory, $"case-probe-{Guid.NewGuid():N}");
        File.WriteAllText(probe, "probe");
        try
        {
            return File.Exists(Path.Combine(directory, Path.GetFileName(probe).ToUpperInvariant()));
        }
        finally
        {
            File.Delete(probe);
        }
    }

    public static string NewDirectory(string prefix = "historia-test-")
    {
        var path = NewPath(prefix);
        Directory.CreateDirectory(path);
        return path;
    }

    public static string NewPath(string prefix = "historia-test-", string suffix = "")
    {
        var runRoot = GetOrCreateRunRoot(Root);
        var hex = Guid.NewGuid().ToString("N")[..12];
        return Path.Combine(runRoot, $"{prefix}{hex}{suffix}");
    }

    private static string GetOrCreateRunRoot(string root)
    {
        _ = PruneOnce.Value;
        _ = ExitHook.Value;
        EnsureGitCeiling(root);

        return RunRoots.GetOrAdd(root, r =>
        {
            var hex = Guid.NewGuid().ToString("N")[..8];
            var dir = Path.Combine(r, $"{RunPrefix}{Environment.ProcessId}-{hex}");
            Directory.CreateDirectory(dir);
            return dir;
        });
    }

    private static void EnsureGitCeiling(string root)
    {
        if (!IsInsideGitWorkTree(root))
            return;

        var existing = Environment.GetEnvironmentVariable("GIT_CEILING_DIRECTORIES");
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.IsNullOrEmpty(existing))
        {
            Environment.SetEnvironmentVariable("GIT_CEILING_DIRECTORIES", normalizedRoot);
        }
        else
        {
            var parts = existing.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            if (!parts.Any(p => string.Equals(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), normalizedRoot, StringComparison.OrdinalIgnoreCase)))
            {
                Environment.SetEnvironmentVariable("GIT_CEILING_DIRECTORIES", $"{existing}{Path.PathSeparator}{normalizedRoot}");
            }
        }
    }

    internal static bool IsInsideGitWorkTree(string path)
    {
        try
        {
            var current = new DirectoryInfo(path);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                    File.Exists(Path.Combine(current.FullName, ".git")))
                {
                    return true;
                }
                current = current.Parent;
            }
        }
        catch
        {
            // Path traversal failures default to false.
        }

        return false;
    }

    /// <summary>
    /// Deletes a file or directory tree, clearing read-only attributes and continuing past
    /// individual file errors. Junctions and symbolic links are removed without touching their
    /// targets. Never throws.
    /// </summary>
    public static void Delete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path))
            {
                try
                {
                    if (!IsLink(new FileInfo(path)))
                        File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
                catch
                {
                    // Best effort.
                }
                return;
            }

            if (Directory.Exists(path))
            {
                var dir = new DirectoryInfo(path);
                if (IsLink(dir))
                {
                    try { dir.Delete(); } catch { }
                    return;
                }

                try
                {
                    ClearReadOnly(dir);
                    dir.Delete(recursive: true);
                    return;
                }
                catch
                {
                    // Fall back to best-effort descent if bulk delete failed.
                }

                DeleteBestEffort(dir);
            }
        }
        catch
        {
            // Best effort; never throws.
        }
    }

    private static void ClearReadOnly(DirectoryInfo dir)
    {
        try
        {
            if ((dir.Attributes & FileAttributes.ReadOnly) != 0)
                dir.Attributes &= ~FileAttributes.ReadOnly;
        }
        catch { }

        try
        {
            foreach (var file in dir.EnumerateFiles())
            {
                try
                {
                    if (!IsLink(file) && (file.Attributes & FileAttributes.ReadOnly) != 0)
                        file.Attributes &= ~FileAttributes.ReadOnly;
                }
                catch { }
            }
        }
        catch { }

        try
        {
            foreach (var sub in dir.EnumerateDirectories())
            {
                if (!IsLink(sub))
                    ClearReadOnly(sub);
            }
        }
        catch { }
    }

    private static void DeleteBestEffort(DirectoryInfo dir)
    {
        try
        {
            foreach (var file in dir.EnumerateFiles())
            {
                try { file.Delete(); } catch { }
            }
        }
        catch { }

        try
        {
            foreach (var sub in dir.EnumerateDirectories())
            {
                if (IsLink(sub))
                {
                    try { sub.Delete(); } catch { }
                }
                else
                {
                    DeleteBestEffort(sub);
                }
            }
        }
        catch { }

        try
        {
            dir.Delete(recursive: true);
        }
        catch { }
    }

    /// <summary>
    /// Deletes the <c>historia-run-*</c> folders of finished test processes that are older than
    /// 30 minutes. Nothing else under the scratch root is touched, and nothing is pruned when the
    /// root is the system temporary directory.
    /// </summary>
    public static void PruneStale()
    {
        try
        {
            var root = Root;
            if (!CanPrune(root))
                return;

            var cutoff = DateTime.UtcNow - StaleAge;
            foreach (var dir in Directory.EnumerateDirectories(root, RunPrefix + "*"))
            {
                try
                {
                    if (RunRoots.Values.Contains(dir, StringComparer.OrdinalIgnoreCase))
                        continue;

                    if (IsRunAlive(Path.GetFileName(dir)))
                        continue;

                    if (new DirectoryInfo(dir).LastWriteTimeUtc < cutoff)
                        Delete(dir);
                }
                catch
                {
                    // Best effort per item.
                }
            }
        }
        catch
        {
            // Best effort.
        }
    }

    internal static bool CanPrune(string root)
    {
        if (!Directory.Exists(root))
            return false;

        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !string.Equals(normalizedRoot, temp, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLink(FileSystemInfo info) =>
        (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static bool IsRunAlive(string folderName)
    {
        if (!folderName.StartsWith(RunPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = folderName[RunPrefix.Length..];
        var dash = rest.IndexOf('-');
        if (dash <= 0 || !int.TryParse(rest[..dash], out var pid) || pid <= 0)
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
}
