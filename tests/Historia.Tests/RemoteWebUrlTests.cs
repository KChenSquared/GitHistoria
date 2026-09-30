namespace Historia.Tests;

[Trait("tier", "unit")]
public class RemoteWebUrlTests
{
    [Theory]
    [InlineData("https://github.com/o/r.git", "https://github.com/o/r")]
    [InlineData("https://user:secret@github.com/o/r.git", "https://github.com/o/r")]
    [InlineData("https://token@github.com/o/r", "https://github.com/o/r")]
    [InlineData("https://git.example.com:8443/o/r.git", "https://git.example.com:8443/o/r")]
    [InlineData("http://git.example.com/o/r.git", "http://git.example.com/o/r")]
    [InlineData("https://github.com/o/r/", "https://github.com/o/r")]
    [InlineData("https://github.com/o/r.git/", "https://github.com/o/r")]
    [InlineData("git@github.com:o/r.git", "https://github.com/o/r")]
    [InlineData("git@github.com:o/r", "https://github.com/o/r")]
    [InlineData("ssh://git@host.example.com:2222/o/r.git", "https://host.example.com/o/r")]
    [InlineData("ssh://git@host.example.com/o/r.git", "https://host.example.com/o/r")]
    public void Maps_remote_urls_to_the_web_page(string url, string expected)
    {
        Assert.True(RemoteWebUrl.TryGet(url, out var web));
        Assert.Equal(expected, web);
    }

    [Theory]
    [InlineData(@"C:\repos\origin")]
    [InlineData("C:/repos/origin.git")]
    [InlineData("/srv/git/origin.git")]
    [InlineData("../sibling")]
    [InlineData("file:///srv/git/origin.git")]
    [InlineData("git://host.example.com/o/r.git")]
    [InlineData("")]
    [InlineData("   ")]
    public void Local_and_unmappable_urls_have_no_web_page(string url)
    {
        Assert.False(RemoteWebUrl.TryGet(url, out var web));
        Assert.Null(web);
    }
}
