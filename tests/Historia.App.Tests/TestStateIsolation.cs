namespace HistoriaApp.Tests;

/// <summary>
/// Redirects <see cref="AppPaths"/> to a throwaway per-test-run directory before any test runs.
/// </summary>
internal static class TestStateIsolation
{
    public static string StateDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "historia-apptests-state-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory.CreateDirectory(StateDirectory);
        AppPaths.OverrideProcessForTesting(StateDirectory);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (Directory.Exists(StateDirectory))
                {
                    foreach (var file in Directory.EnumerateFiles(StateDirectory, "*", SearchOption.AllDirectories))
                        File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(StateDirectory, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        };
    }
}
