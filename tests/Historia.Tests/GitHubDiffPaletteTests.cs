namespace Historia.Tests;

/// <summary>
/// The diff line washes, changed-text washes and gutter tints are GitHub Desktop's Primer values,
/// so added and deleted rows read the same as they do in GitHub Desktop.
/// </summary>
[Trait("tier", "unit")]
public class GitHubDiffPaletteTests
{
    private static void AssertTokens(EffectiveTheme theme, params (string Token, string Hex)[] expected)
    {
        foreach (var (token, hex) in expected)
        {
            Assert.Equal(Rgb.Parse(hex), HistoriaPalette.Get(theme, token));
        }
    }

    [Fact]
    public void Dark_diff_tokens_match_GitHub_Desktop() => AssertTokens(
        EffectiveTheme.Dark,
        ("diff-add-bg", "#113A1B"),
        ("diff-del-bg", "#450C0F"),
        ("diff-add-hl", "#22863A"),
        ("diff-del-hl", "#B31D28"),
        ("diff-add-gutter", "#0B2611"),
        ("diff-del-gutter", "#2F090A"));

    [Fact]
    public void Light_diff_tokens_match_GitHub_Desktop() => AssertTokens(
        EffectiveTheme.Light,
        ("diff-add-bg", "#E6FFED"),
        ("diff-del-bg", "#FFEEF0"),
        ("diff-add-hl", "#ACF2BD"),
        ("diff-del-hl", "#FDB8C0"),
        ("diff-add-gutter", "#CDFFD8"),
        ("diff-del-gutter", "#FFDCE0"));

    [Fact]
    public void High_contrast_gutters_equal_its_line_washes()
    {
        Assert.Equal(HistoriaPalette.Get(EffectiveTheme.HighContrast, "diff-add-bg"), HistoriaPalette.Get(EffectiveTheme.HighContrast, "diff-add-gutter"));
        Assert.Equal(HistoriaPalette.Get(EffectiveTheme.HighContrast, "diff-del-bg"), HistoriaPalette.Get(EffectiveTheme.HighContrast, "diff-del-gutter"));
    }

    [Theory]
    [InlineData("diff-add-gutter", "ColorDiffAddGutter")]
    [InlineData("diff-del-gutter", "ColorDiffDelGutter")]
    public void Gutter_tokens_resolve_to_resource_keys(string token, string key)
    {
        Assert.Contains(token, HistoriaPalette.Tokens);
        Assert.Equal(key, HistoriaPalette.ResourceKey(token));
    }
}
