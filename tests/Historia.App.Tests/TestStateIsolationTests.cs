namespace HistoriaApp.Tests;

/// <summary>
/// Verifies that test state isolation redirects all persistent paths away from the user's real data.
/// </summary>
public sealed class TestStateIsolationTests
{
    [Fact]
    public void DataAndMachineDirectories_AreUnderTemp()
    {
        Assert.Equal(TestStateIsolation.StateDirectory, AppPaths.DataDirectory);
        Assert.Equal(TestStateIsolation.StateDirectory, AppPaths.MachineDirectory);

        var docsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Historia");
        var appDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Historia");

        Assert.False(AppPaths.DataDirectory.StartsWith(docsRoot, StringComparison.OrdinalIgnoreCase));
        Assert.False(AppPaths.MachineDirectory.StartsWith(appDataRoot, StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public async Task RecentsWrittenByWorkspace_LandInIsolatedDir()
    {
        var settings = SettingsService.Load();
        var tempRepoPath = Path.Combine(TestStateIsolation.StateDirectory, "test-repo-" + Guid.NewGuid().ToString("N"));
        var recentFile = AppPaths.RecentRepositoriesFile;

        Assert.StartsWith(TestStateIsolation.StateDirectory, recentFile, StringComparison.OrdinalIgnoreCase);

        var recentService = new RecentRepositoryService(settings);
        await using (recentService)
        {
            await recentService.AddAsync(tempRepoPath, "Test Repo");
        }

        Assert.True(File.Exists(recentFile));
        var content = (await File.ReadAllTextAsync(recentFile)).Replace(@"\\", @"\");
        Assert.Contains(tempRepoPath, content, StringComparison.OrdinalIgnoreCase);
    }
}
