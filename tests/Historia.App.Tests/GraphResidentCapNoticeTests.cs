namespace HistoriaApp.Tests;

/// <summary>
/// When the user attempts to jump to a commit beyond the loaded graph, the notification
/// explains the resident ceiling. In Lightweight mode, it specifically identifies
/// Lightweight mode as the cause and explains how to lift the limit in Settings.
/// </summary>
[Trait("tier", "ui")]
public class GraphResidentCapNoticeTests
{
    [Fact]
    public void Notice_without_lightweight_is_unchanged()
    {
        var notice = CommitGraphControl.ResidentCapNotice(2_500_000, lightweight: false);

        Assert.Equal("History is capped at 2,500,000 rows to preserve memory. Use Find (Ctrl+F) to jump straight to it.", notice);
    }

    [Fact]
    public void Notice_with_lightweight_says_how_to_lift_the_cap()
    {
        var notice = CommitGraphControl.ResidentCapNotice(30_000, lightweight: true);

        Assert.Contains("Lightweight mode", notice);
        Assert.Contains("Settings → Advanced", notice);
        Assert.Contains("30,000", notice);
        Assert.Contains("Find (Ctrl+F)", notice);
        Assert.Equal("History is capped at 30,000 rows by Lightweight mode. Turn it off in Settings → Advanced to load more, or use Find (Ctrl+F) to jump straight to it.", notice);
    }

    [Fact]
    public void Cap_reached_notice_without_lightweight_is_unchanged()
    {
        var notice = CommitGraphControl.ResidentCapReachedNotice(2_500_000, lightweight: false);

        Assert.Equal("Loaded 2,500,000 commits. Further history is capped to preserve system memory.", notice);
    }

    [Fact]
    public void Cap_reached_notice_with_lightweight_says_how_to_lift_the_cap()
    {
        var notice = CommitGraphControl.ResidentCapReachedNotice(30_000, lightweight: true);

        Assert.Equal("Loaded 30,000 commits. Lightweight mode caps history; turn it off in Settings → Advanced to load more.", notice);
    }
}
