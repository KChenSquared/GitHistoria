namespace Historia.Tests;

/// <summary>
/// Scratch root for test runs. Fixtures live under a scratch root inside each test project's
/// build output directory, isolating them from the operating system's temporary directory.
/// Each test process creates a per-run folder deleted on exit, and direct children older than
/// 12 hours are purged on first use.
/// </summary>
public static class TestScratch
{
    private const string RunPrefix = "historia-run-";

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

    private static readonly Lazy<bool> ExitHook = new(() =>
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var runRoot in RunRoots.Values)
                Delete(runRoot);
        };
        return true;
    });

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
    /// individual file errors. Never throws.
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
                    if ((file.Attributes & FileAttributes.ReadOnly) != 0)
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
                DeleteBestEffort(sub);
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
    /// Deletes direct children of the scratch root older than 12 hours, preserving the current
    /// run's directory and any run folder belonging to an active process.
    /// </summary>
    public static void PruneStale()
    {
        try
        {
            var root = Root;
            if (!Directory.Exists(root))
                return;

            var cutoff = DateTime.UtcNow.AddHours(-12);
            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                try
                {
                    if (RunRoots.Values.Contains(entry, StringComparer.OrdinalIgnoreCase))
                        continue;

                    var name = Path.GetFileName(entry);
                    if (IsRunAlive(name))
                        continue;

                    var writeTime = Directory.Exists(entry)
                        ? new DirectoryInfo(entry).LastWriteTimeUtc
                        : new FileInfo(entry).LastWriteTimeUtc;

                    if (writeTime < cutoff)
                        Delete(entry);
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
