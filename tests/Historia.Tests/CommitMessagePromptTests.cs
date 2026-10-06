namespace Historia.Tests;

[Trait("tier", "unit")]
public class CommitMessagePromptTests
{
    private static readonly IReadOnlyList<(string Status, string Path, string? OldPath)> Files =
    [
        ("M", "a.txt", null),
        ("R", "new name.txt", "old name.txt"),
        ("D", "z.txt", null),
    ];

    private static IReadOnlyList<(string Path, string Patch)> Diffs(params (string, string)[] d) => d;

    [Fact]
    public void Unlimited_includes_every_diff_in_path_order()
    {
        var prompt = CommitMessagePrompt.Build("main", Files,
            Diffs(("z.txt", "ZZZ"), ("a.txt", "AAA"), ("new name.txt", "NNN")), null, "");

        Assert.Contains("AAA", prompt);
        Assert.Contains("NNN", prompt);
        Assert.Contains("ZZZ", prompt);
        Assert.True(prompt.IndexOf("### a.txt", StringComparison.Ordinal) < prompt.IndexOf("### new name.txt", StringComparison.Ordinal));
        Assert.True(prompt.IndexOf("### new name.txt", StringComparison.Ordinal) < prompt.IndexOf("### z.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(CommitMessagePrompt.OmittedDiffNote, prompt);
    }

    [Fact]
    public void The_file_list_is_always_complete_and_formats_renames()
    {
        var prompt = CommitMessagePrompt.Build("main", Files, Diffs(), 1, "");

        Assert.Contains("M\ta.txt", prompt);
        Assert.Contains("R\told name.txt -> new name.txt", prompt);
        Assert.Contains("D\tz.txt", prompt);
        Assert.Contains("Current branch: \"main\"", prompt);
    }

    [Fact]
    public void A_cap_keeps_whole_files_only_and_lists_the_rest_as_omitted()
    {
        var small = new string('s', 10);
        var big = new string('b', 100);

        var prompt = CommitMessagePrompt.Build("main", Files,
            Diffs(("a.txt", small), ("new name.txt", big), ("z.txt", small)), 50, "");

        Assert.Contains(small, prompt);
        Assert.DoesNotContain(big, prompt);
        // The first file past the cap and everything after it are omitted, even if a later one would fit.
        Assert.Equal(2, CountOf(prompt, CommitMessagePrompt.OmittedDiffNote));
        Assert.Contains("### z.txt", prompt);
    }

    [Fact]
    public void A_cap_is_measured_in_utf8_bytes()
    {
        var patch = new string('é', 30); // 60 bytes

        var prompt = CommitMessagePrompt.Build("main", Files, Diffs(("a.txt", patch)), 50, "");

        Assert.DoesNotContain(patch, prompt);
        Assert.Contains(CommitMessagePrompt.OmittedDiffNote, prompt);
    }

    [Fact]
    public void The_additional_prompt_is_included()
    {
        var prompt = CommitMessagePrompt.Build("main", Files, Diffs(), null, "- write in French");
        Assert.Contains("- write in French", prompt);
    }

    [Theory]
    [InlineData("feat: x", "feat: x")]
    [InlineData("  feat: x\r\n\r\n- a\r\n", "feat: x\n\n- a")]
    [InlineData("```\nfeat: x\n\n- a\n```", "feat: x\n\n- a")]
    [InlineData("```text\nfeat: x\n```\n", "feat: x")]
    public void Clean_strips_fences_and_whitespace(string raw, string expected) =>
        Assert.Equal(expected, CommitMessagePrompt.Clean(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    [InlineData("```\n```")]
    public void Clean_rejects_an_empty_reply(string? raw)
    {
        var ex = Assert.Throws<AiException>(() => CommitMessagePrompt.Clean(raw));
        Assert.Equal(AiErrorKind.EmptyResponse, ex.Kind);
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
