namespace Historia.Tests;

/// <summary>T3.3 of the debloat audit: the graph column-header pointer geometry lifted out of
/// <c>CommitGraphControl</c>.</summary>
public class GraphColumnInteractionTests
{
    // Refs [0,100)  Graph [100,300)  Message [300,700)
    private static readonly ResolvedColumn[] Cols =
    [
        new(GraphColumnKind.Refs, 0, 100),
        new(GraphColumnKind.Graph, 100, 200),
        new(GraphColumnKind.Message, 300, 400),
    ];

    [Theory]
    [InlineData(100, 0)]     // right on the Refs|Graph edge
    [InlineData(103, 0)]     // within the 4 px grab
    [InlineData(297, 1)]     // near the Graph|Message edge
    [InlineData(106, -1)]    // past the grab, between edges
    [InlineData(700, -1)]    // the trailing edge is not a resizable boundary
    public void BoundaryAt_finds_the_grabbed_edge(double x, int expected)
    {
        Assert.Equal(expected, GraphColumnInteraction.BoundaryAt(Cols, x, hScrollOffset: 0, grab: 4));
    }

    [Fact]
    public void BoundaryAt_applies_the_horizontal_scroll_offset()
    {
        // Edge 100 scrolled left by 40 sits at screen x 60.
        Assert.Equal(0, GraphColumnInteraction.BoundaryAt(Cols, 60, hScrollOffset: 40, grab: 4));
        Assert.Equal(-1, GraphColumnInteraction.BoundaryAt(Cols, 100, hScrollOffset: 40, grab: 4));
    }

    [Theory]
    [InlineData(10, 0)]      // left of every mid-point (mids: 50, 200, 500)
    [InlineData(120, 1)]     // past Refs' mid, left of Graph's
    [InlineData(300, 2)]     // past Graph's mid, left of Message's
    [InlineData(690, 3)]     // past every mid — drop at the end
    public void CaretIndexAt_is_the_first_column_whose_midpoint_is_right_of_x(double x, int expected)
    {
        Assert.Equal(expected, GraphColumnInteraction.CaretIndexAt(Cols, x, hScrollOffset: 0));
    }

    [Theory]
    [InlineData(0, GraphColumnKind.Refs)]
    [InlineData(99, GraphColumnKind.Refs)]
    [InlineData(100, GraphColumnKind.Graph)]   // left inclusive
    [InlineData(299, GraphColumnKind.Graph)]
    [InlineData(300, GraphColumnKind.Message)]
    public void ColumnKindAt_returns_the_column_under_x(double x, GraphColumnKind expected)
    {
        Assert.Equal(expected, GraphColumnInteraction.ColumnKindAt(Cols, x, hScrollOffset: 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(700)]
    [InlineData(5000)]
    public void ColumnKindAt_is_null_outside_every_column(double x)
    {
        Assert.Null(GraphColumnInteraction.ColumnKindAt(Cols, x, hScrollOffset: 0));
    }

    private static IReadOnlyList<GraphColumn> Defaults() => GraphColumnDefaults.All;
    private static string Order(IReadOnlyList<GraphColumn> cols) => string.Join(",", cols.Select(c => c.Kind));

    [Fact]
    public void Reorder_moves_a_visible_column_left_keeping_the_hidden_tail()
    {
        var r = GraphColumnInteraction.Reorder(Defaults(), GraphColumnKind.Graph, caret: 0);
        Assert.Equal("Graph,Refs,Message,Author,Date,Sha", Order(r));
    }

    [Fact]
    public void Reorder_moves_a_visible_column_right_with_the_caret_shift()
    {
        // Refs from index 0, caret 3 -> insert after removal at min(3-1, 2) = 2.
        var r = GraphColumnInteraction.Reorder(Defaults(), GraphColumnKind.Refs, caret: 3);
        Assert.Equal("Graph,Message,Refs,Author,Date,Sha", Order(r));
    }

    [Fact]
    public void Reorder_to_the_same_slot_returns_an_equal_but_fresh_list()
    {
        var input = Defaults();
        var r = GraphColumnInteraction.Reorder(input, GraphColumnKind.Graph, caret: 1);
        Assert.Equal(Order(input), Order(r));
        Assert.NotSame(input, r);   // the control persists on any move, no-op included
    }

    [Fact]
    public void Reorder_of_a_hidden_column_returns_the_input_unchanged()
    {
        var input = Defaults();
        var r = GraphColumnInteraction.Reorder(input, GraphColumnKind.Author, caret: 0);
        Assert.Same(input, r);
    }

    [Fact]
    public void Reorder_clamps_a_caret_past_the_end()
    {
        var r = GraphColumnInteraction.Reorder(Defaults(), GraphColumnKind.Refs, caret: 99);
        Assert.Equal("Graph,Message,Refs,Author,Date,Sha", Order(r));
    }
}
