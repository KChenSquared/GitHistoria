namespace Historia.Tests;

public class NetworkOptionDefaultsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Prune_follows_the_setting(bool prune)
    {
        var settings = new FakeSettingsService(new AppSettings { PruneOnFetch = prune });
        var fetchOpts = GitFetchOptions.From(settings.Current);
        var pullOpts = GitPullOptions.From(settings.Current);

        Assert.Equal(prune, fetchOpts.Prune);
        Assert.Equal(prune, pullOpts.Prune);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Rebase_follows_the_setting(bool rebase)
    {
        var settings = new FakeSettingsService(new AppSettings { PullWithRebase = rebase });
        var pullOpts = GitPullOptions.From(settings.Current);

        Assert.Equal(rebase, pullOpts.Rebase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Background_flag_survives_the_mapping(bool background)
    {
        var settings = new FakeSettingsService(new AppSettings { PruneOnFetch = true });
        var opts = GitFetchOptions.From(settings.Current, background: background);

        Assert.True(opts.Prune);
        Assert.Equal(background, opts.Background);
    }

    [Fact]
    public void Explicit_dialog_choice_overrides_the_rebase_setting()
    {
        var settings = new FakeSettingsService(new AppSettings { PullWithRebase = false });
        var pullOpts = GitPullOptions.From(settings.Current) with { Rebase = true };
        Assert.True(pullOpts.Rebase);

        var settings2 = new FakeSettingsService(new AppSettings { PullWithRebase = true });
        var pullOpts2 = GitPullOptions.From(settings2.Current) with { Rebase = false };
        Assert.False(pullOpts2.Rebase);
    }
}
