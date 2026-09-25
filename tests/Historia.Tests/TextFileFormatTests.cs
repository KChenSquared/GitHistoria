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

    [Fact]
    public void Utf32_bom_is_reported_unsupported()
    {
        var utf32Le = new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x61, 0x00, 0x00, 0x00 };
        Assert.False(TextFileFormat.TryDetect(utf32Le, out _, out _));

        var utf32Be = new byte[] { 0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, 0x61 };
        Assert.False(TextFileFormat.TryDetect(utf32Be, out _, out _));
    }

    [Fact]
    public void Encode_lone_surrogate_does_not_crash_save()
    {
        var originalBytes = "test"u8.ToArray();
        var detected = TextFileFormat.TryDetect(originalBytes, out var format, out _);
        Assert.True(detected);

        // Lone surrogate character
        string textWithLoneSurrogate = "hello \uD800 world";
        var encoded = format.Encode(textWithLoneSurrogate);
        Assert.NotNull(encoded);
        Assert.NotEmpty(encoded);
    }

    [Fact]
    public void Encode_utf16le_with_lone_surrogate_stays_utf16()
    {
        var preamble = new byte[] { 0xFF, 0xFE };
        var sample = preamble.Concat(Encoding.Unicode.GetBytes("valid text")).ToArray();
        var detected = TextFileFormat.TryDetect(sample, out var format, out _);
        Assert.True(detected);
        Assert.True(format.HasBom);

        string textWithLoneSurrogate = "hello \uD800 world";
        var encoded = format.Encode(textWithLoneSurrogate);

        Assert.Equal(0xFF, encoded[0]);
        Assert.Equal(0xFE, encoded[1]);
        Assert.True(encoded.Length % 2 == 0);

        var decoded = Encoding.Unicode.GetString(encoded.AsSpan(2));
        Assert.Equal("hello \uFFFD world", decoded);
    }

    [Fact]
    public void Encode_utf8_bom_and_utf16be_roundtrip()
    {
        // UTF-8 with BOM
        {
            var preamble = new byte[] { 0xEF, 0xBB, 0xBF };
            var original = preamble.Concat("Hello UTF8"u8.ToArray()).ToArray();
            Assert.True(TextFileFormat.TryDetect(original, out var format, out var text));
            var encoded = format.Encode(text);
            Assert.Equal(original, encoded);
        }

        // UTF-16 BE with BOM
        {
            var preamble = new byte[] { 0xFE, 0xFF };
            var original = preamble.Concat(Encoding.BigEndianUnicode.GetBytes("Hello UTF16BE")).ToArray();
            Assert.True(TextFileFormat.TryDetect(original, out var format, out var text));
            var encoded = format.Encode(text);
            Assert.Equal(original, encoded);
        }
    }
}

