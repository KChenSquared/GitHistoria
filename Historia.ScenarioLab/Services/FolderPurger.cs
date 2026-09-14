namespace Historia.ScenarioLab.Services;

public sealed record PurgeResult(int DeletedCount, List<string> Failures);

/// <summary>
/// Safely deletes the contents of scratch folders, clearing read-only flags (e.g. for git packfiles)
/// and verifying destructive guardrails.
/// </summary>
public static class FolderPurger
{
    public static int CountTopLevel(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return 0;

        try
        {
            return Directory.EnumerateFileSystemEntries(path).Count();
        }
        catch
        {
            return 0;
        }
    }

    public static void ValidateGuards(string path, string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty.");

        if (!Path.IsPathRooted(path))
            throw new ArgumentException($"Path '{path}' must be absolute.");

        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Cannot purge drive root '{path}'.");

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full, userProfile, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Cannot purge user profile root '{path}'.");

        var cleanRepo = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full, cleanRepo, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(cleanRepo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Cannot purge folder inside the repository '{path}'.");
        }

        // Ancestor of lab root
        var labRoot = Path.GetFullPath(LabPaths.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (labRoot.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Cannot purge ancestor of ScenarioLab root '{path}'.");
        }
    }

    public static PurgeResult PurgeContents(string path, string repoRoot)
    {
        ValidateGuards(path, repoRoot);

        if (!Directory.Exists(path))
            return new PurgeResult(0, new List<string>());

        int deletedCount = 0;
        var failures = new List<string>();

        var dirInfo = new DirectoryInfo(path);

        foreach (var entry in dirInfo.EnumerateFileSystemInfos())
        {
            try
            {
                if (entry is DirectoryInfo subDir)
                {
                    DeleteDirectoryRecursive(subDir, failures, ref deletedCount);
                }
                else
                {
                    ClearReadOnly(entry);
                    entry.Delete();
                    deletedCount++;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{entry.FullName}: {ex.Message}");
            }
        }

        return new PurgeResult(deletedCount, failures);
    }

    private static void DeleteDirectoryRecursive(DirectoryInfo dir, List<string> failures, ref int deletedCount)
    {
        ClearReadOnly(dir);

        foreach (var child in dir.EnumerateFileSystemInfos())
        {
            try
            {
                if (child is DirectoryInfo subDir)
                {
                    DeleteDirectoryRecursive(subDir, failures, ref deletedCount);
                }
                else
                {
                    ClearReadOnly(child);
                    child.Delete();
                    deletedCount++;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{child.FullName}: {ex.Message}");
            }
        }

        try
        {
            ClearReadOnly(dir);
            dir.Delete(recursive: false);
            deletedCount++;
        }
        catch (Exception ex)
        {
            failures.Add($"{dir.FullName}: {ex.Message}");
        }
    }

    private static void ClearReadOnly(FileSystemInfo info)
    {
        try
        {
            if (info.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                info.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
        catch
        {
        }
    }
}
