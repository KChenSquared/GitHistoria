namespace Historia.Tests;

[Trait("tier", "git")]

public sealed class MiniHostTests
{
    [Fact]
    public async Task Git_executable_is_resolved_once_per_process()
    {
        await using var host1 = new MiniHost();
        await using var host2 = new MiniHost();
        await using var host3 = new MiniHost();

        var loc1 = host1.Provider.GetRequiredService<IGitExecutableLocator>();
        var loc2 = host2.Provider.GetRequiredService<IGitExecutableLocator>();
        var loc3 = host3.Provider.GetRequiredService<IGitExecutableLocator>();

        Assert.Same(loc1, loc2);
        Assert.Same(loc2, loc3);
        Assert.True(loc1.Current.Found);
    }
}
