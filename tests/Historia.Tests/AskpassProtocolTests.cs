namespace Historia.Tests;

// The askpass helper must refuse to
// answer a prompt naming any host other than the one credentials were resolved for.
[Trait("tier", "unit")]
public class AskpassProtocolTests
{
    [Theory]
    [InlineData("Username for 'https://github.com': ", "github.com")]
    [InlineData("Password for 'https://user@github.com': ", "github.com")]
    [InlineData("Password for 'https://github.com:443/owner/repo.git': ", "github.com")]
    public void Matches_the_host_named_in_the_prompt(string prompt, string expectedHost)
        => Assert.True(AskpassProtocol.PromptHostMatches(prompt, expectedHost));

    [Theory]
    [InlineData("Password for 'https://evil.example': ", "github.com")]
    [InlineData("Password for 'https://github.com.evil.example': ", "github.com")]
    [InlineData("Password for 'https://user@evil.example/owner/repo.git': ", "github.com")]
    public void Refuses_a_prompt_naming_a_different_host(string prompt, string expectedHost)
        => Assert.False(AskpassProtocol.PromptHostMatches(prompt, expectedHost));

    [Fact]
    public void Refuses_when_no_host_was_pinned()
        => Assert.False(AskpassProtocol.PromptHostMatches("Password for 'https://github.com': ", null));

    [Fact]
    public void Refuses_an_unparseable_prompt()
        => Assert.False(AskpassProtocol.PromptHostMatches("Password: ", "github.com"));
}
