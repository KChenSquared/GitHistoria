namespace Historia.Tests;

public class GitExecutableLocatorTests
{
    [Fact]
    public void Version_parsing_extracts_major_minor_patch()
    {
        var v1 = GitExecutableLocator.ParseVersion("git version 2.51.0.windows.2");
        Assert.NotNull(v1);
        Assert.Equal(new Version(2, 51, 0), v1);

        var v2 = GitExecutableLocator.ParseVersion("git version 2.30.9");
        Assert.NotNull(v2);
        Assert.Equal(new Version(2, 30, 9), v2);
    }

    [Fact]
    public void Git_version_floor_2_35_rejects_older_and_accepts_newer()
    {
        var older = GitExecutableLocator.ParseVersion("git version 2.34.8");
        var newer = GitExecutableLocator.ParseVersion("git version 2.51.0.windows.2");

        Assert.NotNull(older);
        Assert.NotNull(newer);

        Assert.True(older < GitExecutableInfo.MinimumVersion);
        Assert.True(newer >= GitExecutableInfo.MinimumVersion);

        var olderInfo = new GitExecutableInfo
        {
            Found = false,
            Path = "C:\\Git\\cmd\\git.exe",
            Version = older,
            NotFoundReason = $"Git {older} was found at 'C:\\Git\\cmd\\git.exe', but Git {GitExecutableInfo.MinimumVersion} or newer is required.",
        };
        Assert.False(olderInfo.Found);
        Assert.False(olderInfo.MeetsMinimumVersion);
        Assert.Contains("2.34.8", olderInfo.NotFoundReason);
        Assert.Contains("2.35", olderInfo.NotFoundReason);

        var newerInfo = new GitExecutableInfo
        {
            Found = true,
            Path = "C:\\Git\\cmd\\git.exe",
            Version = newer,
        };
        Assert.True(newerInfo.Found);
        Assert.True(newerInfo.MeetsMinimumVersion);
    }

    [Fact]
    public void ParseVersion_2_34_is_below_minimum()
    {
        var version = GitExecutableLocator.ParseVersion("git version 2.34.1");

        Assert.NotNull(version);
        Assert.True(version < GitExecutableInfo.MinimumVersion);
    }

    [Fact]
    public void ParseVersion_2_35_meets_minimum()
    {
        var version = GitExecutableLocator.ParseVersion("git version 2.35.0.windows.1");

        Assert.NotNull(version);
        Assert.True(version >= GitExecutableInfo.MinimumVersion);
    }

    [Fact]
    public async Task WhenResolvedAsync_completes_upon_resolution()
    {
        var locator = new GitExecutableLocator();
        var waitTask = locator.WhenResolvedAsync();

        Assert.False(waitTask.IsCompleted);

        var resolved = await locator.ResolveAsync(null);
        var awaited = await waitTask;

        Assert.Equal(resolved.Found, awaited.Found);
        Assert.Equal(resolved.Path, awaited.Path);
    }

    [Fact]
    public async Task WhenResolvedAsync_completes_not_found_when_resolution_fails()
    {
        var locator = new GitExecutableLocator();
        var waitTask = locator.WhenResolvedAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            locator.ResolveAsync(null, new CancellationToken(canceled: true)));

        var awaited = await waitTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(awaited.Found);
        Assert.NotNull(awaited.NotFoundReason);
    }
}
