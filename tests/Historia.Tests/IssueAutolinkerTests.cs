using Historia.Models;
using Historia.Services.Text;
using Xunit;

namespace Historia.Tests;

public class IssueAutolinkerTests
{
    private static IssueAutolinkRule Rule(string p, string u) => new() { Pattern = p, UrlTemplate = u };

    [Fact]
    public void NoRulesReturnsSinglePlainRun()
    {
        var runs = IssueAutolinker.Linkify("fixes #12", []);
        Assert.Single(runs);
        Assert.Null(runs[0].Url);
        Assert.Equal("fixes #12", runs[0].Text);
    }

    [Fact]
    public void SubstitutesCaptureGroupIntoTemplate()
    {
        var runs = IssueAutolinker.Linkify("fixes #12 today",
            [Rule(@"#(\d+)", "https://x/issues/$1")]);

        Assert.Collection(runs,
            r => { Assert.Equal("fixes ", r.Text); Assert.Null(r.Url); },
            r => { Assert.Equal("#12", r.Text); Assert.Equal("https://x/issues/12", r.Url); },
            r => { Assert.Equal(" today", r.Text); Assert.Null(r.Url); });
    }

    [Fact]
    public void MultipleMatchesAndRulesInOrder()
    {
        var runs = IssueAutolinker.Linkify("JIRA-9 and #4",
        [
            Rule(@"JIRA-(\d+)", "https://j/$1"),
            Rule(@"#(\d+)", "https://g/$1"),
        ]);

        var links = runs.Where(r => r.Url is not null).ToList();
        Assert.Equal(2, links.Count);
        Assert.Equal("https://j/9", links[0].Url);
        Assert.Equal("https://g/4", links[1].Url);
    }

    [Fact]
    public void OverlappingMatchesFirstRuleWins()
    {
        var runs = IssueAutolinker.Linkify("#123",
        [
            Rule(@"#\d+", "https://a/$0"),
            Rule(@"#12", "https://b/"),
        ]);
        Assert.Contains(runs, r => r.Url == "https://a/%23123");
        Assert.DoesNotContain(runs, r => r.Url == "https://b/");
    }

    [Fact]
    public void InvalidPatternIsIgnored()
    {
        var runs = IssueAutolinker.Linkify("#5", [Rule("(", "https://x/$1"), Rule(@"#(\d+)", "https://y/$1")]);
        Assert.Contains(runs, r => r.Url == "https://y/5");
    }
}
