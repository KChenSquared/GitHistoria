namespace Historia.Tests;

public class RemoteUrlConverterTests
{
    [Theory]
    [InlineData("git@github.com:owner/repo.git", RemoteUrlKind.ScpLike)]
    [InlineData("ssh://git@host:2222/owner/repo.git", RemoteUrlKind.SshUrl)]
    [InlineData("https://github.com/owner/repo.git", RemoteUrlKind.Https)]
    [InlineData("file:///c/repos/thing.git", RemoteUrlKind.LocalOrOther)]
    [InlineData(@"C:\repos\thing", RemoteUrlKind.LocalOrOther)]
    [InlineData("git://example.com/thing.git", RemoteUrlKind.LocalOrOther)]
    public void Classify_identifies_each_shape(string url, RemoteUrlKind expected)
        => Assert.Equal(expected, RemoteUrlConverter.Classify(url));

    [Fact]
    public void Https_to_ssh_uses_scp_like_for_default_port()
    {
        var c = RemoteUrlConverter.ToSsh("https://github.com/owner/repo.git");
        Assert.True(c.WasConverted);
        Assert.Equal("git@github.com:owner/repo.git", c.ConvertedUrl);
    }

    [Fact]
    public void Round_trip_https_ssh_https_is_lossless()
    {
        const string original = "https://gitlab.com/group/subgroup/repo.git";
        var ssh = RemoteUrlConverter.ToSsh(original).ConvertedUrl!;
        var back = RemoteUrlConverter.ToHttps(ssh).ConvertedUrl!;
        Assert.Equal(original, back);
    }

    [Fact]
    public void Non_default_ssh_port_is_preserved_as_ssh_url()
    {
        var c = RemoteUrlConverter.ToSsh("https://host:8443/owner/repo.git");
        Assert.Equal("ssh://git@host:8443/owner/repo.git", c.ConvertedUrl);
    }

    [Fact]
    public void Embedded_https_credentials_are_stripped_and_flagged()
    {
        var c = RemoteUrlConverter.ToSsh("https://user:secrettoken@github.com/owner/repo.git");
        Assert.True(c.HadEmbeddedCredentials);
        Assert.DoesNotContain("secrettoken", c.ConvertedUrl);
        Assert.Equal("user@github.com:owner/repo.git", c.ConvertedUrl);
    }

    [Fact]
    public void Local_paths_are_never_converted()
    {
        var c = RemoteUrlConverter.ToSsh(@"C:\repos\thing");
        Assert.False(c.WasConverted);
        Assert.NotNull(c.SkipReason);
    }

    [Fact]
    public void Scp_like_to_https_drops_the_git_user()
    {
        var c = RemoteUrlConverter.ToHttps("git@bitbucket.org:owner/repo.git");
        Assert.Equal("https://bitbucket.org/owner/repo.git", c.ConvertedUrl);
    }

    [Fact]
    public void Ipv6_literal_host_keeps_its_brackets_and_port()
    {
        var c = RemoteUrlConverter.ToSsh("https://[2001:db8::1]:8443/owner/repo.git");
        Assert.Equal("ssh://git@[2001:db8::1]:8443/owner/repo.git", c.ConvertedUrl);
    }

    [Fact]
    public void Ipv6_literal_host_without_port_round_trips_to_https()
    {
        var c = RemoteUrlConverter.ToHttps("ssh://git@[2001:db8::1]/owner/repo.git");
        Assert.Equal("https://[2001:db8::1]/owner/repo.git", c.ConvertedUrl);
    }
}
