namespace Historia.Tests;

[Trait("tier", "git")]
public class RepositoryStateStashChangeTests
{
    [Fact]
    public void StashStamp_changes_on_push_drop_and_clear_and_is_stable_otherwise()
    {
        using var b = TestRepositoryBuilder.FromTemplate("stash-stamp-stability", r =>
            r.WriteFile("f.txt", "base\n").Stage().Commit("c1"));
        var gitDir = Path.Combine(b.Path, ".git");

        var stampInitial = RefStateToken.StashStamp(gitDir);
        var stampInitial2 = RefStateToken.StashStamp(gitDir);
        Assert.Equal(stampInitial, stampInitial2);

        // Push first stash
        b.WriteFile("f.txt", "stash1\n");
        b.Git("stash", "push", "-m", "s1");
        var stampPush1 = RefStateToken.StashStamp(gitDir);
        Assert.NotEqual(stampInitial, stampPush1);

        // Push second stash
        b.WriteFile("f.txt", "stash2\n");
        b.Git("stash", "push", "-m", "s2");
        var stampPush2 = RefStateToken.StashStamp(gitDir);
        Assert.NotEqual(stampPush1, stampPush2);

        // Drop lower stash: stash@{1}
        b.Git("stash", "drop", "stash@{1}");
        var stampDropLower = RefStateToken.StashStamp(gitDir);
        Assert.NotEqual(stampPush2, stampDropLower);

        // Clear stashes
        b.Git("stash", "clear");
        var stampClear = RefStateToken.StashStamp(gitDir);
        Assert.NotEqual(stampDropLower, stampClear);

        var stampClear2 = RefStateToken.StashStamp(gitDir);
        Assert.Equal(stampClear, stampClear2);
    }

    [Fact]
    public async Task External_stash_push_raises_RefsChanged_once_with_the_new_stamp()
    {
        await using var host = new MiniHost();
        using var b = TestRepositoryBuilder.FromTemplate("stash-push-raise", r =>
            r.WriteFile("f.txt", "base\n").Stage().Commit("c1"));
        await using var ctx = host.Open(b.Path);
        var state = ctx.State;

        // Warm up initial refresh so branches/tags are populated
        await state.RefreshRefsAsync();

        var raises = new List<RefsChangedEventArgs>();
        state.RefsChanged += (_, e) => raises.Add(e);

        b.WriteFile("f.txt", "dirty\n");
        b.Git("stash", "push", "-m", "ext-push");

        await state.RefreshRefsAsync();

        var ev = Assert.Single(raises);
        Assert.Equal(state.StashStamp, ev.StashStamp);
    }

    [Fact]
    public async Task Dropping_a_lower_stash_raises_RefsChanged()
    {
        await using var host = new MiniHost();
        using var b = TestRepositoryBuilder.FromTemplate("stash-drop-raise", r =>
            r.WriteFile("f.txt", "base\n").Stage().Commit("c1"));
        b.WriteFile("f.txt", "s1\n");
        b.Git("stash", "push", "-m", "s1");
        b.WriteFile("f.txt", "s2\n");
        b.Git("stash", "push", "-m", "s2");

        await using var ctx = host.Open(b.Path);
        var state = ctx.State;

        // Warm up initial refresh
        await state.RefreshRefsAsync();

        var raises = new List<RefsChangedEventArgs>();
        state.RefsChanged += (_, e) => raises.Add(e);

        b.Git("stash", "drop", "stash@{1}");

        await state.RefreshRefsAsync();

        var ev = Assert.Single(raises);
        Assert.Equal(state.StashStamp, ev.StashStamp);
    }

    [Fact]
    public async Task Refresh_with_nothing_changed_raises_nothing()
    {
        await using var host = new MiniHost();
        using var b = TestRepositoryBuilder.FromTemplate("stash-no-change-raise", r =>
            r.WriteFile("f.txt", "base\n").Stage().Commit("c1"));
        await using var ctx = host.Open(b.Path);
        var state = ctx.State;

        // First refresh on a MiniHost context with no repo cache does raise because branches go from empty to populated
        await state.RefreshRefsAsync();

        var raises = new List<RefsChangedEventArgs>();
        state.RefsChanged += (_, e) => raises.Add(e);

        await state.RefreshRefsAsync();

        Assert.Empty(raises);
    }
}
