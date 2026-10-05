namespace Historia.Tests;

/// <summary>
/// <c>AppSettings.ResidentRowCap</c> used to be read raw at every
/// call site (<c>GraphLayoutService</c>, <c>CommitGraphControl.ResidentRowCap</c>) — a
/// hand-edited or otherwise out-of-range value (<c>0</c>, or a negative number) would silently stop the background spill and the graph from ever extending.
/// <see cref="GraphLimits.GetMaxResidentRows"/> now clamps through
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
}
