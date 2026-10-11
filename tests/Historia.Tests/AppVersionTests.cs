namespace Historia.Tests;

[Trait("tier", "unit")]
public class AppVersionTests
{
    [Fact]
    public void Display_StripsBuildMetadata()
    {
        Assert.Equal("0.4.0", AppVersion.Display("0.4.0+abc123"));
    }

    [Fact]
    public void Display_KeepsPrerelease()
    {
        Assert.Equal("0.4.1-alpha.0.3", AppVersion.Display("0.4.1-alpha.0.3+abc"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Display_NullOrBlank_ReturnsZero(string? input)
    {
        Assert.Equal("0.0.0", AppVersion.Display(input));
    }

    [Fact]
    public void Current_ComesFromMinVer()
    {
        var stamped = typeof(AppVersion).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "MinVerVersion")
            ?.Value;

        Assert.False(string.IsNullOrWhiteSpace(stamped), "Core was built without MinVer.");
        Assert.Equal(AppVersion.Display(stamped), AppVersion.Current);
    }
}
