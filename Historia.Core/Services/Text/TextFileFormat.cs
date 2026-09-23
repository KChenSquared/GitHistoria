namespace Historia.Services.Text;

/// <summary>
/// Detected text encoding and byte-order mark (BOM) information for a file.
/// </summary>
public readonly record struct TextFileFormat(Encoding Encoding, bool HasBom)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8WithBom = new(true);
    private static readonly UnicodeEncoding Utf16LeStrict = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding Utf16LeWithBom = new(bigEndian: false, byteOrderMark: true);
    private static readonly UnicodeEncoding Utf16BeStrict = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding Utf16BeWithBom = new(bigEndian: true, byteOrderMark: true);

    public static bool TryDetect(ReadOnlySpan<byte> bytes, out TextFileFormat format, out string text)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            try
            {
                text = StrictUtf8.GetString(bytes.Slice(3));
                format = new TextFileFormat(Utf8WithBom, HasBom: true);
                return true;
            }
            catch (DecoderFallbackException)
            {
                format = default;
                text = string.Empty;
                return false;
            }
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            try
            {
                text = Utf16LeStrict.GetString(bytes.Slice(2));
                format = new TextFileFormat(Utf16LeWithBom, HasBom: true);
                return true;
            }
            catch (DecoderFallbackException)
            {
                format = default;
                text = string.Empty;
                return false;
            }
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            try
            {
                text = Utf16BeStrict.GetString(bytes.Slice(2));
                format = new TextFileFormat(Utf16BeWithBom, HasBom: true);
                return true;
            }
            catch (DecoderFallbackException)
            {
                format = default;
                text = string.Empty;
                return false;
            }
        }

        try
        {
            text = StrictUtf8.GetString(bytes);
            format = new TextFileFormat(StrictUtf8, HasBom: false);
            return true;
        }
        catch (DecoderFallbackException)
        {
            format = default;
            text = string.Empty;
            return false;
        }
    }

    public byte[] Encode(string text)
    {
        var preamble = HasBom ? Encoding.GetPreamble() : [];
        var body = Encoding.GetBytes(text);
        if (preamble.Length == 0)
            return body;

        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }
}
