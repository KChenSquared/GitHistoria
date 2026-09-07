using Historia.Services.Markdown;
using Xunit;

namespace Historia.Tests;

public class MarkdownRendererTests
{
    [Fact]
    public void RendersHeadingsListsAndInline()
    {
        var html = MarkdownRenderer.ToHtml("# Title\n\nsome **bold** and `code`\n\n- one\n- two\n");
        Assert.Contains("<h1>Title</h1>", html);
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<code>code</code>", html);
        Assert.Contains("<li>one</li>", html);
    }

    [Fact]
    public void FencedCodeBlockIsEscapedNotInterpreted()
    {
        var html = MarkdownRenderer.ToHtml("```\n<script>alert(1)</script>\n```");
        Assert.Contains("<pre class=\"md-code\"><code>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public void RawHtmlNeverPassesThrough()
    {
        var html = MarkdownRenderer.ToHtml("hello <img src=\"http://evil/x\" onerror=\"alert(1)\"> world");
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;img", html);
    }

    [Fact]
    public void JavascriptLinkIsNeutralised()
    {
        var html = MarkdownRenderer.ToHtml("[click](javascript:alert(1))");
        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("<a ", html);
        Assert.Contains("click", html);
    }

    [Fact]
    public void HttpLinkBecomesADataHrefAnchorThatDoesNotNavigate()
    {
        var html = MarkdownRenderer.ToHtml("[docs](https://example.com/page)");
        Assert.Contains("data-href=\"https://example.com/page\"", html);
        Assert.DoesNotContain(" href=", html); // no plain href → WebView won't navigate itself
    }

    [Fact]
    public void MarkdownImageRendersAsSafeNoteNotAnImgTag()
    {
        var html = MarkdownRenderer.ToHtml("![alt text](https://example.com/pic.png)");
        Assert.DoesNotContain("<img", html);
        Assert.Contains("[image: alt text]", html);
    }

    [Fact]
    public void GfmTableRenders()
    {
        var html = MarkdownRenderer.ToHtml("| a | b |\n|---|---|\n| 1 | 2 |\n");
        Assert.Contains("<table class=\"md-table\">", html);
        Assert.Contains("<th>a</th>", html);
        Assert.Contains("<td>1</td>", html);
    }

    [Fact]
    public void EmptyInputIsEmptyOutput()
    {
        Assert.Equal("", MarkdownRenderer.ToHtml(null));
        Assert.Equal("", MarkdownRenderer.ToHtml("   \n  "));
    }
}
