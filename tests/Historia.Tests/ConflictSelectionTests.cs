namespace Historia.Tests;

public class ConflictSelectionTests
{
    private static ConflictSelection Make(int ours = 2, int theirs = 3)
        => new(
            Enumerable.Range(1, ours).Select(i => $"ours {i}").ToList(),
            Enumerable.Range(1, theirs).Select(i => $"theirs {i}").ToList());

    private static ConflictSide[] FirstSides(ConflictSelection s)
        => s.Layout.Select(r => r.Side).Distinct().ToArray();

    private static (ConflictSide Side, int Index, bool Included)[] Selection(ConflictSelection s)
        => s.Layout.Select(r => (r.Side, r.Index, s.IsIncluded(r.Side, r.Index))).ToArray();

    [Fact]
    public void New_selection_keeps_nothing_and_lists_ours_first()
    {
        var s = Make();
        Assert.True(s.IsEmpty);
        Assert.Equal(new[] { ConflictSide.Ours, ConflictSide.Theirs }, s.SideLayout);
        Assert.Equal(5, s.Layout.Count);
        Assert.Equal((ConflictSide.Ours, 0), s.Layout[0]);
        Assert.Equal((ConflictSide.Theirs, 2), s.Layout[4]);
    }

    [Fact]
    public void SetHunk_include_checks_all_rows()
    {
        var s = Make();
        s.SetHunk(ConflictSide.Theirs, true);
        Assert.Equal(ConflictHunkState.Checked, s.HunkState(ConflictSide.Theirs));
        Assert.All(Enumerable.Range(0, 3), i => Assert.True(s.IsIncluded(ConflictSide.Theirs, i)));
        Assert.Equal(ConflictHunkState.Unchecked, s.HunkState(ConflictSide.Ours));
    }

    [Fact]
    public void SetHunk_exclude_resets_rows()
    {
        var s = Make();
        s.SetHunk(ConflictSide.Ours, true);
        s.ToggleRow(ConflictSide.Ours, 1);
        Assert.Equal(ConflictHunkState.Partial, s.HunkState(ConflictSide.Ours));

        s.SetHunk(ConflictSide.Ours, false);
        Assert.Equal(ConflictHunkState.Unchecked, s.HunkState(ConflictSide.Ours));
        Assert.False(s.IsIncluded(ConflictSide.Ours, 0));
        Assert.False(s.IsIncluded(ConflictSide.Ours, 1));
    }

    [Fact]
    public void Excluding_last_row_unchecks_hunk()
    {
        var s = Make();
        s.SetHunk(ConflictSide.Ours, true);
        s.ToggleRow(ConflictSide.Ours, 0);
        Assert.True(s.IsChecked(ConflictSide.Ours));
        s.ToggleRow(ConflictSide.Ours, 1);
        Assert.False(s.IsChecked(ConflictSide.Ours));
        Assert.Equal(ConflictHunkState.Unchecked, s.HunkState(ConflictSide.Ours));
    }

    [Fact]
    public void Including_one_row_of_unchecked_side_is_partial()
    {
        var s = Make();
        s.SetHunk(ConflictSide.Ours, true);
        s.ToggleRow(ConflictSide.Theirs, 1);
        Assert.Equal(ConflictHunkState.Partial, s.HunkState(ConflictSide.Theirs));
        Assert.True(s.IsIncluded(ConflictSide.Theirs, 1));
    }

    [Fact]
    public void Order_follows_click_order()
    {
        var s = Make();
        s.SetHunk(ConflictSide.Theirs, true);
        s.SetHunk(ConflictSide.Ours, true);
        Assert.Equal(new[] { ConflictSide.Theirs, ConflictSide.Ours }, s.SideLayout);
        Assert.Equal(ConflictSide.Theirs, s.Layout[0].Side);
        Assert.Equal(new[] { ConflictSide.Theirs, ConflictSide.Ours }, FirstSides(s));
    }

    [Fact]
    public void Rechecked_side_goes_after_the_checked_one()
    {
        var s = Make();
        s.SetHunk(ConflictSide.Ours, true);
        s.SetHunk(ConflictSide.Theirs, true);
        Assert.Equal(new[] { ConflictSide.Ours, ConflictSide.Theirs }, s.SideLayout);

        s.SetHunk(ConflictSide.Ours, false);
        s.SetHunk(ConflictSide.Ours, true);
        Assert.Equal(new[] { ConflictSide.Theirs, ConflictSide.Ours }, s.SideLayout);
    }

    [Fact]
    public void Unchecking_never_changes_layout()
    {
        var s = Make();
        s.SetHunk(ConflictSide.Theirs, true);
        Assert.Equal(new[] { ConflictSide.Theirs, ConflictSide.Ours }, s.SideLayout);

        s.SetHunk(ConflictSide.Theirs, false);
        Assert.Equal(new[] { ConflictSide.Theirs, ConflictSide.Ours }, s.SideLayout);
        s.ToggleRow(ConflictSide.Theirs, 0);
        s.ToggleRow(ConflictSide.Theirs, 0);
        Assert.Equal(new[] { ConflictSide.Theirs, ConflictSide.Ours }, s.SideLayout);
    }

    [Fact]
    public void Checking_side_already_in_place_does_not_move()
    {
        var s = Make();
        s.SetHunk(ConflictSide.Ours, true);
        Assert.Equal(new[] { ConflictSide.Ours, ConflictSide.Theirs }, s.SideLayout);
        s.SetHunk(ConflictSide.Theirs, true);
        Assert.Equal(new[] { ConflictSide.Ours, ConflictSide.Theirs }, s.SideLayout);

        s.ToggleRow(ConflictSide.Theirs, 0);
        s.ToggleRow(ConflictSide.Theirs, 0);
        Assert.Equal(new[] { ConflictSide.Ours, ConflictSide.Theirs }, s.SideLayout);
    }

    [Fact]
    public void Empty_side_opposite_lines_has_one_deletion_row()
    {
        var s = Make(ours: 0, theirs: 2);
        Assert.True(s.HasRows(ConflictSide.Ours));
        Assert.Equal(1, s.RowCount(ConflictSide.Ours));
        Assert.True(s.IsDeletion(ConflictSide.Ours));
        Assert.False(s.IsDeletion(ConflictSide.Theirs));
        Assert.True(s.IsDeletionRow(ConflictSide.Ours, 0));
        Assert.Equal(string.Empty, s.RowText(ConflictSide.Ours, 0));

        s.SetHunk(ConflictSide.Ours, true);
        Assert.True(s.IsIncluded(ConflictSide.Ours, 0));
        Assert.Equal(ConflictHunkState.Checked, s.HunkState(ConflictSide.Ours));
        Assert.False(s.IsEmpty);
        Assert.False(s.IsSaved(ConflictSide.Ours, 0));
    }

    [Fact]
    public void Deletion_then_addition_layout_follows_clicks()
    {
        var s = Make(ours: 0, theirs: 2);
        s.SetHunk(ConflictSide.Ours, true);
        s.SetHunk(ConflictSide.Theirs, true);
        Assert.Equal(new[] { (ConflictSide.Ours, 0), (ConflictSide.Theirs, 0), (ConflictSide.Theirs, 1) }, s.Layout);
        Assert.False(s.IsSaved(ConflictSide.Ours, 0));
        Assert.True(s.IsSaved(ConflictSide.Theirs, 0));
        Assert.True(s.IsSaved(ConflictSide.Theirs, 1));
    }

    [Fact]
    public void Addition_then_deletion_keeps_addition_first()
    {
        var s = Make(ours: 0, theirs: 2);
        s.SetHunk(ConflictSide.Theirs, true);
        s.SetHunk(ConflictSide.Ours, true);
        Assert.Equal(new[] { (ConflictSide.Theirs, 0), (ConflictSide.Theirs, 1), (ConflictSide.Ours, 0) }, s.Layout);
    }

    [Fact]
    public void Both_sides_empty_has_no_rows()
    {
        var s = Make(ours: 0, theirs: 0);
        Assert.Equal(0, s.RowCount(ConflictSide.Ours));
        Assert.Equal(0, s.RowCount(ConflictSide.Theirs));
        Assert.False(s.HasRows(ConflictSide.Ours));
        Assert.False(s.HasRows(ConflictSide.Theirs));
        Assert.False(s.IsDeletion(ConflictSide.Ours));
        Assert.False(s.IsDeletion(ConflictSide.Theirs));
        Assert.True(s.IsEmpty);
    }

    [Fact]
    public void Both_sides_non_empty_has_no_deletion()
    {
        var s = Make(ours: 2, theirs: 3);
        Assert.False(s.IsDeletion(ConflictSide.Ours));
        Assert.False(s.IsDeletion(ConflictSide.Theirs));
    }

    [Fact]
    public void Clone_keeps_deletion_side()
    {
        var s = Make(ours: 0, theirs: 2);
        s.SetHunk(ConflictSide.Ours, true);
        var copy = s.Clone();
        Assert.Equal(Selection(s), Selection(copy));
        Assert.True(copy.IsDeletion(ConflictSide.Ours));
        Assert.False(copy.IsDeletion(ConflictSide.Theirs));
    }

    [Fact]
    public void SetOrder_puts_side_first()
    {
        var s = Make();
        s.SetHunk(ConflictSide.Ours, true);
        s.SetHunk(ConflictSide.Theirs, true);
        s.SetOrder(ConflictSide.Theirs);
        Assert.Equal((ConflictSide.Theirs, 0), s.Layout[0]);
        Assert.Equal((ConflictSide.Ours, 1), s.Layout[4]);
    }

    [Fact]
    public void Clone_is_independent()
    {
        var s = Make();
        s.SetHunk(ConflictSide.Ours, true);
        var copy = s.Clone();
        var cloned = Selection(copy);
        Assert.Equal(Selection(s), cloned);

        s.SetHunk(ConflictSide.Theirs, true);
        s.SetOrder(ConflictSide.Theirs);
        Assert.NotEqual(Selection(s), Selection(copy));
        Assert.Equal(cloned, Selection(copy));
        Assert.Equal(ConflictHunkState.Unchecked, copy.HunkState(ConflictSide.Theirs));
        Assert.Equal(new[] { ConflictSide.Ours, ConflictSide.Theirs }, copy.SideLayout);
    }
}
