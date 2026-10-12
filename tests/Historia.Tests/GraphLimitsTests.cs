namespace Historia.Tests;

/// <summary>
/// <c>AppSettings.ResidentRowCap</c> read raw at a call site (<c>GraphLayoutService</c>,
/// <c>CommitGraphControl.ResidentRowCap</c>) would let a hand-edited or otherwise out-of-range
/// value (<c>0</c>, or a negative number) silently stop the background spill and the graph from
/// ever extending. <see cref="GraphLimits.GetMaxResidentRows"/> clamps through
/// <see cref="AppSettings.ClampResidentRowCap"/> centrally, so every caller is protected without
/// re-clamping itself.
/// </summary>
[Trait("tier", "unit")]
public class GraphLimitsTests
{
    [Fact]
    public void Disk_mode_clamps_a_too_low_cap_up_to_the_memory_floor()
    {
        var result = GraphLimits.GetMaxResidentRows(metaFromDisk: true, residentRowCapDisk: 0);

        Assert.Equal(500_000, result);
        Assert.Equal(AppSettings.MinResidentRowCap, result);
    }

    [Fact]
    public void Disk_mode_has_no_upper_ceiling()
    {
        Assert.Equal(20_000_000, GraphLimits.GetMaxResidentRows(metaFromDisk: true, residentRowCapDisk: 20_000_000));
        Assert.Equal(int.MaxValue, GraphLimits.GetMaxResidentRows(metaFromDisk: true, residentRowCapDisk: int.MaxValue));
    }

    [Fact]
    public void Disk_mode_clamps_a_negative_cap_up_to_the_memory_floor()
    {
        Assert.Equal(AppSettings.MinResidentRowCap, GraphLimits.GetMaxResidentRows(metaFromDisk: true, residentRowCapDisk: -1));
    }

    [Fact]
    public void Disk_mode_passes_through_a_value_already_in_range()
    {
        Assert.Equal(3_000_000, GraphLimits.GetMaxResidentRows(metaFromDisk: true, residentRowCapDisk: 3_000_000));
    }

    [Fact]
    public void Memory_mode_ignores_the_disk_cap_argument()
    {
        Assert.Equal(GraphLimits.MaxResidentRowsMemory, GraphLimits.GetMaxResidentRows(metaFromDisk: false, residentRowCapDisk: 0));
        Assert.Equal(GraphLimits.MaxResidentRowsMemory, GraphLimits.GetMaxResidentRows(metaFromDisk: false, residentRowCapDisk: 999_999_999));
    }

    [Fact]
    public void Lightweight_caps_resident_rows_at_30k_in_disk_mode()
    {
        Assert.Equal(30_000, GraphLimits.GetMaxResidentRows(metaFromDisk: true, residentRowCapDisk: 2_500_000, lightweight: true));
        Assert.Equal(30_000, GraphLimits.GetMaxResidentRows(metaFromDisk: true, residentRowCapDisk: 0, lightweight: true));
        Assert.Equal(30_000, GraphLimits.GetMaxResidentRows(metaFromDisk: true, residentRowCapDisk: 20_000_000, lightweight: true));
    }

    [Fact]
    public void Lightweight_caps_resident_rows_at_30k_in_memory_mode()
    {
        Assert.Equal(30_000, GraphLimits.GetMaxResidentRows(metaFromDisk: false, residentRowCapDisk: 0, lightweight: true));
        Assert.Equal(30_000, GraphLimits.GetMaxResidentRows(metaFromDisk: false, residentRowCapDisk: 999_999_999, lightweight: true));
    }

    [Fact]
    public void Lightweight_off_matches_the_two_argument_overload()
    {
        int[] testCaps = [0, -1, 500_000, 2_500_000, 20_000_000];
        foreach (var cap in testCaps)
        {
            Assert.Equal(GraphLimits.GetMaxResidentRows(metaFromDisk: true, residentRowCapDisk: cap),
                GraphLimits.GetMaxResidentRows(metaFromDisk: true, residentRowCapDisk: cap, lightweight: false));
            Assert.Equal(GraphLimits.GetMaxResidentRows(metaFromDisk: false, residentRowCapDisk: cap),
                GraphLimits.GetMaxResidentRows(metaFromDisk: false, residentRowCapDisk: cap, lightweight: false));
        }
    }

    [Fact]
    public void Lightweight_preload_is_10k_whatever_is_stored()
    {
        Assert.Equal(10_000, GraphLimits.GetSilentCrawlRowCap(0, lightweight: true));
        Assert.Equal(10_000, GraphLimits.GetSilentCrawlRowCap(500_000, lightweight: true));
        Assert.Equal(10_000, GraphLimits.GetSilentCrawlRowCap(5, lightweight: true));
    }

    [Fact]
    public void Preload_passes_the_stored_value_through_when_lightweight_is_off()
    {
        Assert.Equal(0, GraphLimits.GetSilentCrawlRowCap(0, lightweight: false));
        Assert.Equal(500_000, GraphLimits.GetSilentCrawlRowCap(500_000, lightweight: false));
        Assert.Equal(123_456, GraphLimits.GetSilentCrawlRowCap(123_456, lightweight: false));
    }
}
