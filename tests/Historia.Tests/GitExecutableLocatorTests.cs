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
    public void Git_version_floor_2_46_rejects_older_and_accepts_newer()
    {
        var older = GitExecutableLocator.ParseVersion("git version 2.45.2");
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
        Assert.Contains("2.45.2", olderInfo.NotFoundReason);
        Assert.Contains("2.46", olderInfo.NotFoundReason);

        var newerInfo = new GitExecutableInfo
        {
            Found = true,
            Path = "C:\\Git\\cmd\\git.exe",
            Version = newer,
        };
        Assert.True(newerInfo.Found);
    }

    [Fact]
    public void ParseVersion_2_45_is_below_minimum()
    {
        var version = GitExecutableLocator.ParseVersion("git version 2.45.2");

        Assert.NotNull(version);
        Assert.True(version < GitExecutableInfo.MinimumVersion);
    }

    [Fact]
    public void ParseVersion_2_46_meets_minimum()
    {
        var version = GitExecutableLocator.ParseVersion("git version 2.46.0.windows.1");

        Assert.NotNull(version);
        Assert.True(version >= GitExecutableInfo.MinimumVersion);
    }

    [Fact]
    public void Executable_name_matches_the_platform()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? "git.exe" : "git", GitExecutableLocator.ExecutableName);
    }

    [Fact]
    public void FindOnPath_returns_the_first_directory_holding_the_executable()
    {
        var root = TestScratch.NewDirectory("historia-locator-");
        var empty = Path.Combine(root, "empty");
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            File.WriteAllText(Path.Combine(first, GitExecutableLocator.ExecutableName), "");
            File.WriteAllText(Path.Combine(second, GitExecutableLocator.ExecutableName), "");
            var pathVar = string.Join(Path.PathSeparator, empty, first, second);

            Assert.Equal(Path.Combine(first, GitExecutableLocator.ExecutableName), GitExecutableLocator.FindOnPath(pathVar));
            Assert.Null(GitExecutableLocator.FindOnPath(empty));
            Assert.Null(GitExecutableLocator.FindOnPath(null));
        }
        finally
        {
            TestScratch.Delete(root);
        }
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
