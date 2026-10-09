namespace Historia.Diagnostics;

/// <summary>
/// Retention and trimming logic for application and performance log files.
/// Operates directly on the filesystem with no switch dependencies.
/// </summary>
internal static class LogRetention
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>
    /// Deletes session log files (<c>dev-*.log</c>) older than <paramref name="cutoff"/>,
    /// skipping <paramref name="activeSessionPath"/>.
    /// Returns counts of stale files found and removed. Throws on the first deletion failure
    /// of an existing stale file.
    /// </summary>
    public static (int Found, int Removed) TrimDevLogs(string dir, DateTime cutoff, string? activeSessionPath)
    {
        if (!Directory.Exists(dir))
            return (0, 0);

        string? normalizedActive = null;
        if (!string.IsNullOrEmpty(activeSessionPath))
        {
            try { normalizedActive = Path.GetFullPath(activeSessionPath); }
            catch { /* best effort */ }
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(dir, "dev-*.log");
        }
        catch (DirectoryNotFoundException)
        {
            return (0, 0);
        }

        var found = 0;
        var removed = 0;

        foreach (var file in files)
        {
            if (normalizedActive is not null)
            {
                try
                {
                    if (string.Equals(Path.GetFullPath(file), normalizedActive, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                catch { /* best effort */ }
            }

            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (!info.Exists)
                    continue;
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            if (info.LastWriteTime < cutoff)
            {
                found++;
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (FileNotFoundException)
                {
                    // Vanished between listing and delete is not a failure.
                }
                catch (DirectoryNotFoundException)
                {
                    // Vanished between listing and delete is not a failure.
                }
            }
        }

        return (found, removed);
    }

    /// <summary>
    /// Trims <paramref name="path"/> to entries newer than <paramref name="cutoff"/>.
    /// If the file itself is older than cutoff by last-write time, it is deleted whole.
    /// Returns true if stale content was found and removed, false if untouched.
    /// </summary>
    public static bool TrimPerfFile(string path, DateTime cutoff)
    {
        if (!File.Exists(path))
            return false;

        var fi = new FileInfo(path);
        if (!fi.Exists)
            return false;

        if (fi.LastWriteTime < cutoff)
        {
            File.Delete(path);
            return true;
        }

        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
            dir = ".";
        var tempPath = Path.Combine(dir, $"{Path.GetFileName(path)}.trim-{Guid.NewGuid():N}.tmp");

        var staleFound = false;
        var currentKept = false;

        try
        {
            using (var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
            using (var writer = new StreamWriter(new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (TryParseTimestamp(line, out var stamp))
                    {
                        if (stamp >= cutoff)
                        {
                            currentKept = true;
                            writer.WriteLine(line);
                        }
                        else
                        {
                            currentKept = false;
                            staleFound = true;
                        }
                    }
                    else
                    {
                        if (currentKept)
                        {
                            writer.WriteLine(line);
                        }
                        else
                        {
                            staleFound = true;
                        }
                    }
                }
            }

            if (!staleFound)
            {
                try { File.Delete(tempPath); } catch { }
                return false;
            }

            try
            {
                File.Replace(tempPath, path, null);
            }
            catch (PlatformNotSupportedException)
            {
                File.Move(tempPath, path, overwrite: true);
            }

            return true;
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private static bool TryParseTimestamp(string line, out DateTime timestamp)
    {
        if (line.Length >= 23 &&
            DateTime.TryParseExact(
                line.AsSpan(0, 23),
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out timestamp))
        {
            return true;
        }

        timestamp = default;
        return false;
    }
}
