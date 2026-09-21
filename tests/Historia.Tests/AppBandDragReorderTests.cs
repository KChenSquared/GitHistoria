namespace Historia.Tests;

/// <summary>T3.6 of the debloat audit: the app-band tab drag geometry lifted out of
/// <c>AppBandControl</c>.</summary>
[Trait("tier", "unit")]
public class AppBandDragReorderTests
{
    [Theory]
    [InlineData(100, 103, false)]   // a click jitter under the 4 px threshold
    [InlineData(100, 104, true)]    // exactly the threshold
    [InlineData(100, 90, true)]     // travel the other way
    public void PassesThreshold_flips_a_click_into_a_drag(double press, double now, bool expected)
    {
        Assert.Equal(expected, AppBandDragReorder.PassesThreshold(press, now, threshold: 4));
    }

    // Three 100 px tabs -> mid-points at 50, 150, 250.
    private static readonly double[] Three = [100, 100, 100];

    [Theory]
    [InlineData(0, 0)]
    [InlineData(49, 0)]
    [InlineData(51, 1)]     // past tab 0's mid-point
    [InlineData(160, 2)]    // past tab 1's mid-point
    [InlineData(400, 2)]    // way past the end -> clamped to the last index
    public void TargetIndex_lands_on_the_slot_whose_midpoint_the_cursor_passed(double panelX, int expected)
    {
        Assert.Equal(expected, AppBandDragReorder.TargetIndex(panelX, Three, fallbackWidth: 186));
    }

    [Fact]
    public void TargetIndex_uses_the_fallback_width_for_unmeasured_tabs()
    {
        double[] widths = [0, 0, 0];   // nothing laid out yet
        // fallback 100 -> mid-points at 50, 150, 250, same as the measured case.
        Assert.Equal(1, AppBandDragReorder.TargetIndex(60, widths, fallbackWidth: 100));
        Assert.Equal(0, AppBandDragReorder.TargetIndex(40, widths, fallbackWidth: 100));
    }

    [Theory]
    [InlineData(10, -1)]    // inside the left zone
    [InlineData(28, 0)]     // exactly at the zone edge -> neutral
    [InlineData(400, 0)]    // mid-strip
    [InlineData(775, 1)]    // inside the right zone (width 800, zone 28 -> trigger past 772)
    public void EdgeDirection_triggers_only_inside_a_zone(double viewportX, int expected)
    {
        Assert.Equal(expected, AppBandDragReorder.EdgeDirection(viewportX, viewportWidth: 800, zone: 28));
    }

    // §F9: a linked child's live drag target is clamped into its own group's span
    // [groupStart + 1, groupStart + groupCount - 1] — group index 2, three-member group (owner
    // at 2, two children at 3 and 4) unless stated otherwise.
    [Theory]
    [InlineData(0, 3)]    // below the group entirely -> clamps to the first legal sibling slot
    [InlineData(2, 3)]    // at the owner's own slot -> still clamps to the first sibling slot
    [InlineData(10, 4)]   // above the group entirely -> clamps to the last legal sibling slot
    [InlineData(3, 3)]    // already inside the span -> unchanged
    public void ClampChildTarget_clamps_into_the_groups_own_span(int rawTarget, int expected)
    {
        Assert.Equal(expected, AppBandDragReorder.ClampChildTarget(rawTarget, groupStart: 2, groupCount: 3));
    }

    [Fact]
    public void ClampChildTarget_returns_the_group_start_sentinel_for_a_single_member_group()
    {
        // No siblings to reorder among -> every raw target clamps to groupStart itself, the
        // caller's "no legal move" sentinel.
        Assert.Equal(5, AppBandDragReorder.ClampChildTarget(rawTarget: 0, groupStart: 5, groupCount: 1));
        Assert.Equal(5, AppBandDragReorder.ClampChildTarget(rawTarget: 99, groupStart: 5, groupCount: 1));
    }

    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(0, 2, 3)]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(1, 2, 2)]
    [InlineData(1, 1, 1)]
    [InlineData(2, 0, 0)]
    [InlineData(2, 1, 1)]
    [InlineData(2, 2, 3)]
    public void BlockInsertIndex_calculates_flat_insertion_index_after_removal(int current, int target, int expected)
    {
        int[] sizes = [1, 2, 1];
        Assert.Equal(expected, AppBandDragReorder.BlockInsertIndex(sizes, current, target));
    }

    [Fact]
    public void BlockInsertIndex_never_lands_inside_a_group()
    {
        int[] sizes = [1, 3, 2, 1];
        for (var current = 0; current < sizes.Length; current++)
        {
            var remaining = sizes.Where((_, idx) => idx != current).ToList();
            var validBoundaries = new HashSet<int> { 0 };
            var acc = 0;
            foreach (var s in remaining)
            {
                acc += s;
                validBoundaries.Add(acc);
            }

            for (var target = 0; target < sizes.Length; target++)
            {
                var result = AppBandDragReorder.BlockInsertIndex(sizes, current, target);
                Assert.Contains(result, validBoundaries);
            }
        }
    }
}
