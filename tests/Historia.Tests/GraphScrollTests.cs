namespace Historia.Tests;

[Trait("tier", "unit")]
public class GraphScrollTests
{
    [Fact]
    public void ContentShorterThanViewportReturnsNull()
    {
        // 10 rows * 22 = 220 px in a 600 px viewport -> null (R12: no scrollbar, no move)
        var offset = GraphScroll.CenterOffset(row: 5, rowHeight: 22, viewportHeight: 600, contentHeight: 220);
        Assert.Null(offset);
    }

    [Fact]
    public void ContentExactlyViewportHeightReturnsNull()
    {
        // Exactly viewport height: max == 0 -> null
        var offset = GraphScroll.CenterOffset(row: 5, rowHeight: 20, viewportHeight: 500, contentHeight: 500);
        Assert.Null(offset);
    }

    [Fact]
    public void MiddleRowCentresExactly()
    {
        const double rowH = 20;
        const double viewH = 400;
        const double contentH = 20_000; // 1000 rows
        const int row = 500;

        var offset = GraphScroll.CenterOffset(row, rowH, viewH, contentH);
        Assert.NotNull(offset);

        // Ideal offset = 500 * 20 + 10 - 200 = 10000 + 10 - 200 = 9810
        Assert.Equal(9810, offset.Value);
    }

    [Fact]
    public void RowNearTopClampsToZero()
    {
        const double rowH = 20;
        const double viewH = 400;
        const double contentH = 2000;
        const int row = 2; // ideal: 2 * 20 + 10 - 200 = -150 -> clamps to 0

        var offset = GraphScroll.CenterOffset(row, rowH, viewH, contentH);
        Assert.NotNull(offset);
        Assert.Equal(0, offset.Value);
    }

    [Fact]
    public void RowNearBottomClampsToMax()
    {
        const double rowH = 20;
        const double viewH = 400;
        const double contentH = 2000;
        const double max = contentH - viewH; // 1600
        const int row = 99; // row 99: 99 * 20 + 10 - 200 = 1790 -> clamps to 1600

        var offset = GraphScroll.CenterOffset(row, rowH, viewH, contentH);
        Assert.NotNull(offset);
        Assert.Equal(max, offset.Value);
    }

    [Fact]
    public void ExactTieLandsAtTop()
    {
        const double rowH = 20;
        const double viewH = 100;
        const double contentH = 1000;
        // Ideal offset = row * rowH + rowH / 2 - viewH / 2
        // To be exactly 0: row * 20 + 10 - 50 = 0 -> row * 20 = 40 -> row = 2
        var offset = GraphScroll.CenterOffset(row: 2, rowHeight: rowH, viewportHeight: viewH, contentHeight: contentH);
        Assert.NotNull(offset);
        Assert.Equal(0, offset.Value);
    }

    [Fact]
    public void ResultIsAlwaysWithinRange()
    {
        const double rowH = 22;
        const double viewH = 500;
        const int rowCount = 200;
        const double contentH = rowCount * rowH;
        const double max = contentH - viewH;

        for (int r = 0; r < rowCount; r++)
        {
            var offset = GraphScroll.CenterOffset(r, rowH, viewH, contentH);
            Assert.NotNull(offset);
            Assert.InRange(offset.Value, 0, max);
        }
    }
}
