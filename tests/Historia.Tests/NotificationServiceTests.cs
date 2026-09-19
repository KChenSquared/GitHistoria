namespace Historia.Tests;

/// <summary>§18: identical error notifications published in quick succession coalesce
/// into one card with an occurrence count instead of stacking.</summary>
[Trait("tier", "unit")]
public class NotificationServiceTests
{
    private static NotificationService WithClock(out Action<TimeSpan> advance)
    {
        var now = DateTimeOffset.UnixEpoch;
        advance = d => now += d;
        return new NotificationService(() => now);
    }

    [Fact]
    public void Repeated_identical_publish_within_the_window_yields_one_card_with_a_count()
    {
        var svc = WithClock(out var advance);

        svc.Publish(NotificationSeverity.Error, "Push failed.", "stderr");
        advance(TimeSpan.FromSeconds(1));
        svc.Publish(NotificationSeverity.Error, "Push failed.", "stderr");
        advance(TimeSpan.FromSeconds(1));
        var third = svc.Publish(NotificationSeverity.Error, "Push failed.", "stderr");

        Assert.Single(svc.Current);
        Assert.Equal(3, svc.Current[0].Count);
        Assert.Equal(third.Id, svc.Current[0].Id);
    }

    [Fact]
    public void A_different_message_is_a_separate_card()
    {
        var svc = WithClock(out _);

        svc.Publish(NotificationSeverity.Error, "Push failed.", "stderr");
        svc.Publish(NotificationSeverity.Error, "Pull failed.", "stderr");
        svc.Publish(NotificationSeverity.Warning, "Push failed.", "stderr"); // different severity

        Assert.Equal(3, svc.Current.Count);
        Assert.All(svc.Current, n => Assert.Equal(1, n.Count));
    }

    [Fact]
    public void Outside_the_window_it_is_a_new_card()
    {
        var svc = WithClock(out var advance);

        svc.Publish(NotificationSeverity.Error, "Push failed.", "stderr");
        advance(TimeSpan.FromSeconds(10));
        svc.Publish(NotificationSeverity.Error, "Push failed.", "stderr");

        Assert.Equal(2, svc.Current.Count);
        Assert.All(svc.Current, n => Assert.Equal(1, n.Count));
    }

    [Fact]
    public void Coalescing_still_respects_the_retention_cap()
    {
        var svc = WithClock(out var advance);

        for (var i = 0; i < 12; i++)
        {
            svc.Publish(NotificationSeverity.Info, $"message {i}");
            advance(TimeSpan.FromSeconds(10)); // each distinct + outside the window
        }

        Assert.Equal(6, svc.Current.Count);
        Assert.Equal("message 11", svc.Current[^1].Message);
    }
}
