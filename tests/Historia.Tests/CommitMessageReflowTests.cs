namespace Historia.Tests;

[Trait("tier", "unit")]

public class CommitMessageReflowTests
{
    private const string HardWrapped91df387 =
        "theme: dark menu, flyout, tooltip and scrollbar theming\n" +
        "\n" +
        "Override Fluent's MenuFlyout*, ToolTip* and ScrollBar* resource keys from the\n" +
        "palette in AvaloniaThemeApplier, and style only what those keys can't express\n" +
        "(corner radius, padding, separators) in App.axaml, so no per-instance\n" +
        "pseudo-class or template selectors are added to ScrollBar/MenuItem/TextBlock.\n" +
        "\n" +
        "Hand-rolled dialogs now inherit the app icon (fallback decoded once) and get\n" +
        "DWM caption theming that tracks theme changes while open. ScenarioLab palette\n" +
        "follows the updated dark colors.\n" +
        "\n" +
        "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>\n";

    private const string Lists =
        "subject\n\n" +
        "- first item that wraps\n  onto a second line\n" +
        "* star item\n" +
        "1. numbered item\n   continued\n" +
        "2) paren item\n" +
        "after the list\nstill prose";

    private const string Verbatim =
        "subject\n\n" +
        "prose before\n" +
        "    indented code\n" +
        "\tTabbed code\n" +
        "> quoted line\n> second quote\n" +
        "| a | b |\n| - | - |\n" +
        "```\nfenced line one\nfenced line two\n\nafter blank in fence\n```\n" +
        "~~~\ntilde fence\n~~~\n" +
        "prose after";

    private const string Trailers =
        "subject\n\nbody line one\nbody line two\n\n" +
        "Signed-off-by: A Person <a@example.com>\n" +
        "Co-Authored-By: B Person <b@example.com>";

    public static TheoryData<string> Fixtures() =>
    [
        HardWrapped91df387,
        "a\nb\n\nbody",
        Lists,
        Verbatim,
        Trailers,
        Trailers + "\n  \n\t",
        "one\r\ntwo\r\n\r\nthree\r\n",
        "single line",
        "",
    ];

    [Fact]
    public void Joins_hard_wrapped_paragraph_lines()
    {
        Assert.Equal(
            "theme: dark menu, flyout, tooltip and scrollbar theming\n" +
            "\n" +
            "Override Fluent's MenuFlyout*, ToolTip* and ScrollBar* resource keys from the " +
            "palette in AvaloniaThemeApplier, and style only what those keys can't express " +
            "(corner radius, padding, separators) in App.axaml, so no per-instance " +
            "pseudo-class or template selectors are added to ScrollBar/MenuItem/TextBlock.\n" +
            "\n" +
            "Hand-rolled dialogs now inherit the app icon (fallback decoded once) and get " +
            "DWM caption theming that tracks theme changes while open. ScenarioLab palette " +
            "follows the updated dark colors.\n" +
            "\n" +
            "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>",
            CommitMessageReflow.Reflow(HardWrapped91df387));
    }

    [Fact]
    public void Joins_a_multi_line_first_paragraph_like_git_subject()
    {
        Assert.Equal("a b\n\nbody", CommitMessageReflow.Reflow("a\nb\n\nbody"));
    }

    [Fact]
    public void Keeps_blank_lines_between_paragraphs()
    {
        Assert.Equal("one two\n\n\nthree", CommitMessageReflow.Reflow("one\ntwo\n\n\nthree"));
    }

    [Fact]
    public void Keeps_list_items_and_joins_their_continuations()
    {
        Assert.Equal(
            "subject\n\n" +
            "- first item that wraps onto a second line\n" +
            "* star item\n" +
            "1. numbered item continued\n" +
            "2) paren item\n" +
            "after the list still prose",
            CommitMessageReflow.Reflow(Lists));
    }

    [Fact]
    public void Keeps_indented_code_quotes_tables_and_fences_verbatim()
    {
        Assert.Equal(Verbatim, CommitMessageReflow.Reflow(Verbatim));
    }

    [Fact]
    public void Keeps_trailer_block_lines_separate()
    {
        Assert.Equal(
            "subject\n\nbody line one body line two\n\n" +
            "Signed-off-by: A Person <a@example.com>\n" +
            "Co-Authored-By: B Person <b@example.com>",
            CommitMessageReflow.Reflow(Trailers));
    }

    [Fact]
    public void Keeps_trailer_block_when_the_message_ends_in_a_whitespace_line()
    {
        Assert.Equal(
            "subject\n\nbody line one body line two\n\n" +
            "Signed-off-by: A Person <a@example.com>\n" +
            "Co-Authored-By: B Person <b@example.com>",
            CommitMessageReflow.Reflow(Trailers + "\n  \n\t"));
    }

    [Fact]
    public void A_conventional_subject_is_not_a_trailer_block()
    {
        Assert.Equal("feat: add x Refs: 12", CommitMessageReflow.Reflow("feat: add x\nRefs: 12"));
    }

    [Fact]
    public void Normalises_crlf()
    {
        Assert.Equal("one two\n\nthree", CommitMessageReflow.Reflow("one\r\ntwo\r\n\r\nthree\r\n"));
        Assert.Equal("one two", CommitMessageReflow.Reflow("one\rtwo"));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Is_idempotent(string raw)
    {
        var once = CommitMessageReflow.Reflow(raw);
        Assert.Equal(once, CommitMessageReflow.Reflow(once));
    }

    [Fact]
    public void Null_and_empty_return_empty()
    {
        Assert.Equal("", CommitMessageReflow.Reflow(null));
        Assert.Equal("", CommitMessageReflow.Reflow(""));
        Assert.Equal("", CommitMessageReflow.Reflow("\n\n"));
    }

    [Fact]
    public void Single_line_message_is_unchanged()
    {
        Assert.Equal("fix: the thing", CommitMessageReflow.Reflow("fix: the thing"));
        Assert.Equal("fix: the thing", CommitMessageReflow.Reflow("fix: the thing\n"));
    }

    [Fact]
    public void Reflowed_text_still_autolinks_issue_refs()
    {
        const string raw = "subject\n\nthis change resolves the crash reported in\n#12 when the graph is empty";
        var reflowed = CommitMessageReflow.Reflow(raw);
        Assert.Equal("subject\n\nthis change resolves the crash reported in #12 when the graph is empty", reflowed);

        var runs = IssueAutolinker.Linkify(reflowed,
            [new IssueAutolinkRule { Pattern = @"#(\d+)", UrlTemplate = "https://x/issues/$1" }]);

        var link = Assert.Single(runs, r => r.Url is not null);
        Assert.Equal("#12", link.Text);
        Assert.Equal("https://x/issues/12", link.Url);
    }
}
