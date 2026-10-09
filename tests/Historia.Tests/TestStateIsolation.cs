namespace Historia.Tests;

/// <summary>
/// Cuts the test run off from the developer machine's own Historia state before any test runs,
/// and records startup state for <see cref="LoggingSwitchTests"/>.
/// The process-wide <see cref="LoggingSwitch"/> starts off; tests turn logging on for their own
/// flow (<c>PerfLog.OverrideEnabledForTesting</c>, <c>AppLog.StartSessionForTesting</c>).
/// </summary>
internal static class TestStateIsolation
{
    public static string StateDirectory { get; } = TestScratch.NewDirectory("historia-state-run-");

    internal static bool LoggingSwitchEnabledAtStartup;

    [ModuleInitializer]
    internal static void Initialize()
    {
        LoggingSwitchEnabledAtStartup = LoggingSwitch.Enabled;

        // Process-local only: the developer's own environment is untouched.
        Environment.SetEnvironmentVariable("HISTORIA_LOG", null);

        AppPaths.OverrideProcessForTesting(StateDirectory);
    }
}
