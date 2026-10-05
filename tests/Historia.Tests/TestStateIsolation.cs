namespace Historia.Tests;

/// <summary>
/// Cuts the test run off from the developer machine's own Historia state before any test runs.
/// <see cref="PerfLog"/> and <see cref="AppLog"/> read their opt-in (<c>HISTORIA_PERF</c> /
/// <c>HISTORIA_LOG</c>, or a marker file under <see cref="AppPaths.MachineDirectory"/>) once, the
/// first time either is touched — so without this, a developer with perf logging switched on in the
/// app would get it switched on for whichever run happened to resolve the flag outside a test's
/// <see cref="StateDir"/>, with every mark from every parallel test appended under one lock.
/// Tests turn logging on for their own flow (<c>PerfLog.OverrideEnabledForTesting</c>,
/// <c>AppLog.StartSessionForTesting</c>); nothing from the machine does.
/// </summary>
internal static class TestStateIsolation
{
    public static string StateDirectory { get; } = TestScratch.NewDirectory("historia-state-run-");

    [ModuleInitializer]
    internal static void Initialize()
    {
        // Process-local only: the developer's own environment is untouched.
        Environment.SetEnvironmentVariable("HISTORIA_PERF", null);
        Environment.SetEnvironmentVariable("HISTORIA_LOG", null);

        AppPaths.OverrideProcessForTesting(StateDirectory);

        // Resolve both flags now, against the run's empty state directory, so their marker paths
        // never point at the machine's %APPDATA%\Historia.
        _ = PerfLog.Enabled;
        _ = AppLog.Enabled;
    }
}
