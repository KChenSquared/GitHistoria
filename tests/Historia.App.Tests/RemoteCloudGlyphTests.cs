namespace HistoriaApp.Tests;

/// <summary>The numbered remote cloud: a filled silhouette cut from the vendored cloud glyph, with
/// the number knocked out of it.</summary>
[Trait("tier", "ui")]
public sealed class RemoteCloudGlyphTests
{
    [AvaloniaFact]
    public void Filled_path_is_the_cloud_outline_up_to_its_first_close()
    {
        var data = RemoteCloudGlyph.FilledPathData;
        var cloud = Historia.Components.IconPaths.Get("cloud");

        Assert.EndsWith("Z", data);
        Assert.Equal(cloud.IndexOf('Z'), data.Length - 1);
        Assert.StartsWith(data, cloud);
        Assert.True(data.Length < cloud.Length);

        var g = RemoteCloudGlyph.CreateFilledGeometry(16);
        Assert.True(g.Bounds.Width is > 12 and <= 16);
        Assert.True(g.Bounds.Bottom <= 16);
    }

    [AvaloniaFact]
    public void A_numbered_icon_shows_its_digit()
    {
        var icon = RemoteCloudGlyph.CreateIcon(2, 14, "ColorTextMuted", "ColorChrome");

        var digit = Assert.Single(icon.GetLogicalDescendants().OfType<TextBlock>());
        Assert.Equal("2", digit.Text);
        Assert.Single(icon.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>());
    }

    [AvaloniaFact]
    public void An_unnumbered_icon_is_a_plain_cloud()
    {
        var icon = RemoteCloudGlyph.CreateIcon(0, 14, "ColorTextMuted", "ColorChrome");

        Assert.Empty(icon.GetLogicalDescendants().OfType<TextBlock>());
        Assert.Single(icon.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>());
    }

    [Fact]
    public void Two_digit_numbers_use_a_smaller_font()
    {
        Assert.True(RemoteCloudGlyph.DigitFontSize(12, 16) < RemoteCloudGlyph.DigitFontSize(2, 16));
        Assert.Equal(RemoteCloudGlyph.DigitFontSize(1, 16), RemoteCloudGlyph.DigitFontSize(9, 16));
    }

    [Fact]
    public void Digit_band_sits_inside_the_box()
    {
        var band = RemoteCloudGlyph.DigitBand(16);
        Assert.True(band.X >= 0 && band.Right <= 16 && band.Y >= 0 && band.Bottom <= 16);
    }

    [Fact]
    public void Add_glyph_resolves() =>
        Assert.StartsWith("M", Historia.Components.IconPaths.Get("add"));
}
