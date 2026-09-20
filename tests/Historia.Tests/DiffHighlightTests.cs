namespace Historia.Tests;
[Trait("tier", "unit")]

public class SyntaxHighlighterTests
{
    private static string Kinds(string line, string? lang) =>
        string.Join(",", SyntaxHighlighter.Tokenize(line, lang).Select(t => $"{t.Kind}:{line.Substring(t.Start, t.Length)}"));

    [Fact]
    public void EmptyLineHasNoTokens()
    {
        Assert.Empty(SyntaxHighlighter.Tokenize("", "csharp"));
        Assert.Empty(SyntaxHighlighter.Tokenize("   ", "csharp"));
    }

    [Fact]
    public void KeywordsStringsNumbersAndComments()
    {
        var k = Kinds("var x = 42; // note", "csharp");
        Assert.Contains("Keyword:var", k);
        Assert.Contains("Number:42", k);
        Assert.Contains("Comment:// note", k);
    }

    [Fact]
    public void StringLiteralIsOneToken()
    {
        var k = Kinds("s = \"hi there\"", "javascript");
        Assert.Contains("String:\"hi there\"", k);
    }

    [Fact]
    public void PythonUsesHashComments()
    {
        var k = Kinds("x = 1  # comment", "python");
        Assert.Contains("Comment:# comment", k);
        // '#' is not a comment in the C family.
        Assert.DoesNotContain("Comment", Kinds("a # b", "csharp"));
    }

    [Fact]
    public void PlainLanguageProducesNoKeywords()
    {
        Assert.DoesNotContain("Keyword", Kinds("class var int", "text"));
    }
}

[Trait("tier", "unit")]
public class WordDiffTests
{
    [Fact]
    public void IdenticalLinesHaveNoChangedSpans()
    {
        var r = WordDiff.Compute("the quick brown fox", "the quick brown fox");
        Assert.Empty(r.OldChanged);
        Assert.Empty(r.NewChanged);
    }

    [Fact]
    public void OnlyTheChangedWordIsMarked()
    {
        var oldL = "the quick brown fox";
        var newL = "the quick red fox";
        var r = WordDiff.Compute(oldL, newL);

        var oldWord = r.OldChanged.Single();
        Assert.Equal("brown", oldL.Substring(oldWord.Start, oldWord.Length));

        var newWord = r.NewChanged.Single();
        Assert.Equal("red", newL.Substring(newWord.Start, newWord.Length));
    }

    [Fact]
    public void PureInsertionMarksOnlyTheNewSide()
    {
        var r = WordDiff.Compute("a c", "a b c");
        Assert.Empty(r.OldChanged);
        var s = r.NewChanged.Single();
        Assert.Equal("b", "a b c".Substring(s.Start, s.Length));
    }
}
