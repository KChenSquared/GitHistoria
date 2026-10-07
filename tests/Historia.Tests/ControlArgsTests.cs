namespace Historia.Tests;

[Trait("tier", "unit")]
public sealed class ControlArgsTests
{
    private static readonly string Cwd = Path.Combine(Path.GetTempPath(), "ctl-cwd");

    private static ControlArgsResult Parse(params string[] args) => ControlArgs.Parse(args, Cwd);

    [Fact]
    public void Ping_and_selection_take_nothing()
    {
        Assert.Equal(ControlVerb.Ping, Parse("ping").Request!.Verb);
        Assert.Equal(ControlVerb.Selection, Parse("selection").Request!.Verb);
        Assert.True(Parse("ping", "extra").IsUsageError);
        Assert.True(Parse("selection", "--no-launch").IsUsageError);
    }

    [Fact]
    public void Open_defaults_to_the_cwd_and_focus()
    {
        var r = Parse("open");

        Assert.Equal(ControlVerb.Open, r.Request!.Verb);
        Assert.Equal(Cwd, r.Request.Repo);
        Assert.Equal(Cwd, r.Request.Cwd);
        Assert.True(r.Request.WantsFocus);
        Assert.False(r.NoLaunch);
    }

    [Fact]
    public void Show_takes_a_revision()
    {
        var r = Parse("show", "HEAD~2");

        Assert.Equal(ControlVerb.Show, r.Request!.Verb);
        Assert.Equal("HEAD~2", r.Request.Rev);
        Assert.True(Parse("show").IsUsageError);
        Assert.True(Parse("show", "a", "b").IsUsageError);
    }

    [Fact]
    public void Diff_takes_a_revision_a_file_and_a_line()
    {
        var r = Parse("diff", "abc123", "src/a.cs", "--line", "17");

        Assert.Equal(ControlVerb.Diff, r.Request!.Verb);
        Assert.Equal("abc123", r.Request.Rev);
        Assert.Equal("src/a.cs", r.Request.Path);
        Assert.Equal(17, r.Request.Line);
        Assert.True(Parse("diff", "abc123").IsUsageError);
    }

    [Fact]
    public void Compare_takes_base_head_and_file()
    {
        var r = Parse("compare", "main", "HEAD", "a.cs");

        Assert.Equal(ControlVerb.Compare, r.Request!.Verb);
        Assert.Equal("main", r.Request.Rev);
        Assert.Equal("HEAD", r.Request.OtherRev);
        Assert.Equal("a.cs", r.Request.Path);
        Assert.Null(r.Request.Line);
    }

    [Fact]
    public void Changes_takes_a_file_and_optional_staged()
    {
        var r = Parse("changes", "a.cs", "--staged", "--line", "3");

        Assert.Equal(ControlVerb.Changes, r.Request!.Verb);
        Assert.Equal("a.cs", r.Request.Path);
        Assert.True(r.Request.Staged);
        Assert.Equal(3, r.Request.Line);
        Assert.False(Parse("changes", "a.cs").Request!.Staged);
    }

    [Fact]
    public void Flags_apply_after_the_verb_in_any_position()
    {
        var ok = Parse("show", "--no-focus", "HEAD", "--no-launch");

        Assert.False(ok.Request!.WantsFocus);
        Assert.True(ok.NoLaunch);
        Assert.True(Parse("--no-focus", "show", "HEAD").IsUsageError);
    }

    [Fact]
    public void A_relative_repo_option_becomes_absolute_against_the_cwd()
    {
        var r = Parse("show", "HEAD", "-C", "sub");

        Assert.Equal(Path.Combine(Cwd, "sub"), r.Request!.Repo);
        Assert.Equal(Cwd, r.Request.Cwd);
    }

    [Fact]
    public void An_absolute_repo_option_is_kept()
    {
        var abs = Path.Combine(Path.GetTempPath(), "other");

        Assert.Equal(abs, Parse("open", "-C", abs).Request!.Repo);
    }

    [Theory]
    [InlineData("show", "HEAD", "--bogus")]
    [InlineData("show", "HEAD", "-C")]
    [InlineData("diff", "HEAD", "a", "--line")]
    [InlineData("diff", "HEAD", "a", "--line", "x")]
    [InlineData("diff", "HEAD", "a", "--line", "0")]
    [InlineData("diff", "HEAD", "a", "--line", "-4")]
    [InlineData("show", "HEAD", "--line", "3")]
    [InlineData("show", "HEAD", "--staged")]
    [InlineData("nope")]
    [InlineData("SHOW", "HEAD")]
    public void Malformed_command_lines_are_usage_errors(params string[] args)
    {
        var r = Parse(args);

        Assert.True(r.IsUsageError);
        Assert.Null(r.Request);
    }

    [Fact]
    public void No_arguments_is_a_usage_error()
    {
        Assert.True(Parse().IsUsageError);
    }

    [Fact]
    public void Help_and_version_are_recognised()
    {
        Assert.True(Parse("--help").ShowHelp);
        Assert.True(Parse("--version").ShowVersion);
    }

    [Fact]
    public void A_hostile_revision_is_a_usage_error_even_after_the_options_terminator()
    {
        Assert.True(Parse("show", "--", "--all").IsUsageError);
        Assert.True(Parse("show", "-rf").IsUsageError);
        Assert.True(Parse("compare", "main", "--output=x", "a").IsUsageError);
    }

    [Fact]
    public void The_options_terminator_lets_a_dash_file_through()
    {
        var r = Parse("changes", "--", "-odd.txt");

        Assert.Equal("-odd.txt", r.Request!.Path);
    }

    [Fact]
    public void Propose_commit_joins_repeated_messages_with_a_blank_line()
    {
        var r = Parse("propose-commit", "-m", "feat: x", "-m", "Body text.");

        Assert.Equal(ControlVerb.ProposeCommit, r.Request!.Verb);
        Assert.Equal("feat: x\n\nBody text.", r.Request.CommitMessage);
        Assert.Null(r.MessageFile);
    }

    [Fact]
    public void Propose_commit_reads_the_message_from_a_file_or_stdin()
    {
        var stdin = Parse("propose-commit", "-F", "-");
        var file = Parse("propose-commit", "-F", "msg.txt", "--no-launch");

        Assert.Equal("-", stdin.MessageFile);
        Assert.Null(stdin.Request!.CommitMessage);
        Assert.Equal("msg.txt", file.MessageFile);
        Assert.True(file.NoLaunch);
    }

    [Fact]
    public void Propose_commit_needs_exactly_one_message_source()
    {
        Assert.True(Parse("propose-commit").IsUsageError);
        Assert.True(Parse("propose-commit", "-m", "x", "-F", "-").IsUsageError);
        Assert.True(Parse("propose-commit", "-m").IsUsageError);
        Assert.True(Parse("propose-commit", "-F").IsUsageError);
    }

    [Fact]
    public void Propose_commit_rejects_an_empty_or_invalid_inline_message()
    {
        Assert.True(Parse("propose-commit", "--no-launch", "-m", "").IsUsageError);
        Assert.True(Parse("propose-commit", "-m", "bad\u001b").IsUsageError);
    }

    [Fact]
    public void Message_options_belong_to_propose_commit_only()
    {
        Assert.True(Parse("show", "HEAD", "-m", "x").IsUsageError);
        Assert.True(Parse("open", "-F", "-").IsUsageError);
    }

    [Fact]
    public void Usage_text_names_every_verb()
    {
        foreach (var verb in Enum.GetValues<ControlVerb>())
            Assert.Contains("historia-cmdr " + ControlVerbs.ToWire(verb), ControlArgs.UsageText);
    }
}
