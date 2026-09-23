namespace Historia.Tests;

public class InlineDiffTests
{
    [Fact]
    public void Empty_strings_produce_no_changes()
    {
        var changes = InlineDiff.Compare(string.Empty, string.Empty);
        Assert.Empty(changes);
    }

    [Fact]
    public void Insert_at_end_produces_added_span()
    {
        var changes = InlineDiff.Compare("hello", "hello world");
        Assert.Single(changes);
        var change = changes[0];
        Assert.Equal(0, change.DeletedCount);
        Assert.Equal(5, change.AddedStart);
        Assert.Equal(6, change.AddedCount); // " world"
    }

    [Fact]
    public void Delete_at_end_produces_deleted_span()
    {
        var changes = InlineDiff.Compare("hello world", "hello");
        Assert.Single(changes);
        var change = changes[0];
        Assert.Equal(5, change.DeletedStart);
        Assert.Equal(6, change.DeletedCount);
        Assert.Equal(0, change.AddedCount);
    }

    [Fact]
    public void Replace_word_produces_paired_span()
    {
        var changes = InlineDiff.Compare("hello world", "hello there");
        Assert.Single(changes);
        var change = changes[0];
        Assert.Equal(6, change.DeletedStart);
        Assert.Equal(5, change.DeletedCount); // "world"
        Assert.Equal(6, change.AddedStart);
        Assert.Equal(5, change.AddedCount);   // "there"
    }

    [Fact]
    public void CJK_text_differences_are_grouped_properly()
    {
        var changes = InlineDiff.Compare("前缀 你好世界 后缀", "前缀 你好朋友 后缀");
        Assert.Single(changes);
        var change = changes[0];
        Assert.Equal(3, change.DeletedStart);
        Assert.Equal(4, change.DeletedCount); // "你好世界"
        Assert.Equal(3, change.AddedStart);
        Assert.Equal(4, change.AddedCount);   // "你好朋友"
    }

    [Fact]
    public void Identical_strings_produce_no_changes()
    {
        var changes = InlineDiff.Compare("exact match", "exact match");
        Assert.Empty(changes);
    }
}
