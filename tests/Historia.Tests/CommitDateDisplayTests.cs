namespace Historia.Tests;

[Trait("tier", "git")]

public sealed class CommitDateDisplayTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    private static GraphQuery Q => new() { RefScope = GraphRefScope.AllRefs };

    private static List<GitObjectId> RowIds(GraphLayoutResult r) =>
        Enumerable.Range(0, r.Layout.RowCount).Select(i => r.Layout.Commits[i].Id).ToList();

    [Fact]
    public void Pick_returns_commit_date_by_default()
    {
        var author = new DateTimeOffset(2026, 9, 14, 10, 50, 0, TimeSpan.FromHours(-7));
        var commit = new DateTimeOffset(2026, 9, 14, 22, 25, 0, TimeSpan.FromHours(-7));

        var picked = CommitDateDisplay.Pick(author, commit, showAuthorDate: false);

        Assert.Equal(commit, picked);
    }

    [Fact]
    public void Pick_returns_author_date_when_the_setting_is_on()
    {
        var author = new DateTimeOffset(2026, 9, 14, 10, 50, 0, TimeSpan.FromHours(-7));
        var commit = new DateTimeOffset(2026, 9, 14, 22, 25, 0, TimeSpan.FromHours(-7));

        var picked = CommitDateDisplay.Pick(author, commit, showAuthorDate: true);

        Assert.Equal(author, picked);
    }

    [Fact]
    public async Task Toggling_show_author_date_does_not_change_row_order()
    {
        var locator = _host.Provider.GetRequiredService<IGitExecutableLocator>();
        if (!locator.Current.Found)
            return;

        using var repo = TestRepositoryBuilder.FromTemplate("commit-date-display-seed", r =>
        {
            // git commit --date=<past> sets only the author date, so commit dates stay in commit
            // creation order while author dates run opposite to commit dates.
            r.WriteFile("1.txt", "1").Stage();
            r.Git("commit", "-m", "C1", "--date=2026-09-15T12:30:00Z", "--no-gpg-sign");

            r.WriteFile("2.txt", "2").Stage();
            r.Git("commit", "-m", "C2", "--date=2026-09-15T12:20:00Z", "--no-gpg-sign");

            r.WriteFile("3.txt", "3").Stage();
            r.Git("commit", "-m", "C3", "--date=2026-09-15T12:10:00Z", "--no-gpg-sign");
        });

        // Compute with setting off
        await _host.Settings.UpdateAsync(s => s with { ShowAuthorDate = false });
        await using var ctxOff = _host.Open(repo.Path);
        ctxOff.MarkFirstGraphPaintDone(); ctxOff.MarkFirstFullLoadDone();
        var svcOff = new GraphLayoutService(_host.Settings, locator);
        var resultOff = await svcOff.ComputeLayoutAsync(ctxOff, Q);
        var rowsOff = RowIds(resultOff);

        // Compute with setting on
        await _host.Settings.UpdateAsync(s => s with { ShowAuthorDate = true });
        await using var ctxOn = _host.Open(repo.Path);
        ctxOn.MarkFirstGraphPaintDone(); ctxOn.MarkFirstFullLoadDone();
        var svcOn = new GraphLayoutService(_host.Settings, locator);
        var resultOn = await svcOn.ComputeLayoutAsync(ctxOn, Q);
        var rowsOn = RowIds(resultOn);

        Assert.NotEmpty(rowsOff);
        Assert.Equal(rowsOff, rowsOn);
    }
}
