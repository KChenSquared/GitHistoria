namespace Historia.Tests;

/// <summary>§V3-9.1: the two-location portable resolution, the write probe, the machine
/// directory, and the one-time copy migration from a prior %APPDATA% install.</summary>
public sealed class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "historia-apppaths-" + Guid.NewGuid().ToString("N"));

    public AppPathsTests()
    {
        Directory.CreateDirectory(_root);
        AppPaths.OverrideForTesting(null); // ensure a clean, non-overridden state
    }

    [Fact]
    public void Prefers_documents_when_the_probe_succeeds()
    {
        var docs = Path.Combine(_root, "Docs");
        Directory.CreateDirectory(docs);

        var resolved = AppPaths.ResolveFor(() => docs, _ => true);

        Assert.Equal(Path.Combine(docs, "Historia"), resolved);
        Assert.False(AppPaths.PortableResolutionFailed);
    }

    [Fact]
    public void Falls_back_to_configs_beside_the_exe_when_documents_probe_fails()
    {
        var docs = Path.Combine(_root, "Docs");

        var resolved = AppPaths.ResolveFor(
            () => docs,
            dir => dir.EndsWith("configs")); // only the configs\ candidate passes

        Assert.EndsWith("configs", resolved);
        Assert.False(AppPaths.PortableResolutionFailed);
    }

    [Fact]
    public void Reports_and_disables_persistence_when_both_candidates_fail()
    {
        var resolved = AppPaths.ResolveFor(() => Path.Combine(_root, "Docs"), _ => false);

        Assert.True(AppPaths.PortableResolutionFailed);
        Assert.NotNull(resolved);
        Assert.NotEqual("", AppPaths.AttemptedPortablePaths.Documents);
        Assert.NotEqual("", AppPaths.AttemptedPortablePaths.Configs);
    }

    [Fact]
    public void MachineDirectory_is_appdata_historia_and_unaffected_by_the_probe()
    {
        AppPaths.ResolveFor(() => Path.Combine(_root, "Docs"), _ => false); // fail the portable probe

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Historia");
        Assert.Equal(expected, AppPaths.MachineDirectory);
    }

    [Fact]
    public void OverrideForTesting_wins_even_after_an_earlier_read_cached_a_result()
    {
        _ = AppPaths.DataDirectory; // force a real resolution + cache

        var overrideDir = Path.Combine(_root, "override");
        AppPaths.OverrideForTesting(overrideDir);

        Assert.Equal(overrideDir, AppPaths.DataDirectory);
        Assert.Equal(overrideDir, AppPaths.MachineDirectory);
    }

    [Fact]
    public void WipeAllLocalData_clears_both_state_roots_but_keeps_the_folders()
    {
        var dir = Path.Combine(_root, "state");
        Directory.CreateDirectory(dir);
        AppPaths.OverrideForTesting(dir); // DataDirectory == MachineDirectory == dir

        AppPaths.EnsureCreated();
        File.WriteAllText(AppPaths.SettingsFile, "{}");
        File.WriteAllText(Path.Combine(AppPaths.LogsDirectory, "perf.log"), "x");
        File.WriteAllText(Path.Combine(AppPaths.CacheDirectory, "graph-abc.json.gz"), "x");
        Directory.CreateDirectory(AppPaths.ThemesDirectory);
        File.WriteAllText(AppPaths.CustomThemeFile("mine"), "{}");

        var removed = AppPaths.WipeAllLocalData();

        Assert.True(removed >= 4);
        Assert.True(Directory.Exists(dir), "the root folder itself is kept");
        Assert.Empty(Directory.GetFileSystemEntries(dir));
    }

    public void Dispose()
    {
        AppPaths.OverrideForTesting(null);
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
