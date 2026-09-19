namespace Historia.Tests;

/// <summary>
/// Scratch root for test runs (§16.1, F7). Honours <c>HISTORIA_TEST_TMP</c> so the scratch
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

    public static string NewDirectory(string prefix = "historia-test-")
    {
        var root = Root;
        Directory.CreateDirectory(root);
        var dir = Path.Combine(root, prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
