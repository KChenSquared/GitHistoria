namespace Historia.Tests;

[Trait("tier", "unit")]
public class TextFileFormatTests
{
    [Fact]
    public void Utf8_no_bom_round_trip()
    {
        var originalBytes = "Hello\r\nWorld\nMixed\rEndings"u8.ToArray();
        var detected = TextFileFormat.TryDetect(originalBytes, out var format, out var text);

        Assert.True(detected);
        Assert.False(format.HasBom);
        Assert.False(text.StartsWith('\uFEFF'));
        Assert.Equal("Hello\r\nWorld\nMixed\rEndings", text);

        var encoded = format.Encode(text);
        Assert.Equal(originalBytes, encoded);
    }

    [Fact]
    public void Utf8_bom_preserved_and_no_feff_in_text()
    {
        var preamble = new byte[] { 0xEF, 0xBB, 0xBF };
        var body = "Line 1\r\nLine 2"u8.ToArray();
        var originalBytes = preamble.Concat(body).ToArray();

        var detected = TextFileFormat.TryDetect(originalBytes, out var format, out var text);

        Assert.True(detected);
        Assert.True(format.HasBom);
        Assert.False(text.StartsWith('\uFEFF'));
        Assert.Equal("Line 1\r\nLine 2", text);

        var encoded = format.Encode(text);
        Assert.Equal(originalBytes, encoded);
    }

    [Fact]
    public void Utf16_le_bom_round_trip()
    {
        var preamble = new byte[] { 0xFF, 0xFE };
        var textBody = "First line\r\nSecond line\nThird";
        var body = Encoding.Unicode.GetBytes(textBody);
        var originalBytes = preamble.Concat(body).ToArray();

        var detected = TextFileFormat.TryDetect(originalBytes, out var format, out var text);

        Assert.True(detected);
        Assert.True(format.HasBom);
        Assert.False(text.StartsWith('\uFEFF'));
        Assert.Equal(textBody, text);

        var encoded = format.Encode(text);
        Assert.Equal(originalBytes, encoded);
    }

    [Fact]
    public void Utf16_be_bom_round_trip()
    {
        var preamble = new byte[] { 0xFE, 0xFF };
        var textBody = "Alpha\r\nBeta\nGamma";
        var body = Encoding.BigEndianUnicode.GetBytes(textBody);
        var originalBytes = preamble.Concat(body).ToArray();

        var detected = TextFileFormat.TryDetect(originalBytes, out var format, out var text);

        Assert.True(detected);
        Assert.True(format.HasBom);
        Assert.False(text.StartsWith('\uFEFF'));
        Assert.Equal(textBody, text);

        var encoded = format.Encode(text);
        Assert.Equal(originalBytes, encoded);
    }

    [Fact]
    public void Invalid_utf8_is_unsupported()
    {
        // 0xE9 is Latin-1 'é', invalid standalone in UTF-8
        var invalidBytes = new byte[] { 0x61, 0x62, 0xE9, 0x63 };
        var detected = TextFileFormat.TryDetect(invalidBytes, out var format, out var text);

        Assert.False(detected);
        Assert.Equal(string.Empty, text);
    }
}
