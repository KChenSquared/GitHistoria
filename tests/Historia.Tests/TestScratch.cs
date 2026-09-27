namespace Historia.Tests;

/// <summary>
/// Scratch root for test runs. Honours <c>HISTORIA_TEST_TMP</c> so the scratch
/// directory can be pinned to a fixed path and excluded from real-time virus scans.
/// </summary>
public static class TestScratch
{
    public static string Root
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("HISTORIA_TEST_TMP");
            return !string.IsNullOrWhiteSpace(env) ? env : Path.GetTempPath();
        }
    }

    private static readonly Lazy<bool> PruneOnce = new(() =>
    {
        PruneStale();
        return true;
    });

    public static string NewDirectory(string prefix = "historia-test-")
    {
        _ = PruneOnce.Value;
        var root = Root;
        Directory.CreateDirectory(root);
        var dir = Path.Combine(root, prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Deletes test directories older than 24 hours under the scratch root.
    /// Safe against concurrent test processes since it only removes stale artifacts.
    /// </summary>
    public static void PruneStale()
    {
        try
        {
            var root = Root;
            if (!Directory.Exists(root))
                return;

            var cutoff = DateTime.UtcNow.AddHours(-24);
            var patterns = new[] { "historia-tpl-*", "historia-test-*", "historia-state-*" };
            foreach (var pattern in patterns)
            {
                foreach (var dir in Directory.EnumerateDirectories(root, pattern))
                {
                    try
                    {
                        var info = new DirectoryInfo(dir);
                        if (info.LastWriteTimeUtc < cutoff)
                        {
                            ClearReadOnly(info);
                            info.Delete(recursive: true);
                        }
                    }
                    catch
                    {
                        // best effort
                    }
                }
            }
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
