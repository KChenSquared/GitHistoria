namespace HistoriaApp.Tests;

/// <summary>
/// Redirects <see cref="AppPaths"/> to a throwaway per-test-run directory before any test runs.
/// </summary>
internal static class TestStateIsolation
{
    public static string StateDirectory { get; } =
        TestScratch.NewDirectory("historia-apptests-state-");

    [ModuleInitializer]
    internal static void Initialize()
    {
        AppPaths.OverrideProcessForTesting(StateDirectory);
    }
}
