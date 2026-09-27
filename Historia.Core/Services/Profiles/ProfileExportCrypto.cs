namespace Historia.Services.Profiles;

/// <summary>
/// Password-based AES-256-GCM encryption for profile export bundles that include tokens.
/// Exports without tokens are plain JSON and never touch this.
/// </summary>
public static class ProfileExportCrypto
{
    // Format family "EPOCHEXP" + a version digit. v1 = PBKDF2-SHA256 200k; v2 = 600k
    // (2024 OWASP guidance). Encrypt always writes the latest; Decrypt honours whatever the
    // bundle declares, so old exports keep working.
    private const int MagicLength = 9;
    private static readonly byte[] MagicV1 = "EPOCHEXP1"u8.ToArray();
    private static readonly byte[] MagicV2 = "EPOCHEXP2"u8.ToArray();

    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private static int IterationsFor(int version) => version switch
    {
        1 => 200_000,
        2 => 600_000,
        _ => throw new InvalidDataException($"Unsupported Historia export version {version}."),
    };

    public static byte[] Encrypt(string plaintextJson, string password)
    {
        const int version = 2;
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = DeriveKey(password, salt, version);

        var plaintext = Encoding.UTF8.GetBytes(plaintextJson);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

        using var ms = new MemoryStream();
        ms.Write(MagicV2);
        ms.Write(salt);
        ms.Write(nonce);
        ms.Write(tag);
        ms.Write(ciphertext);
        return ms.ToArray();
    }

    public static string Decrypt(byte[] blob, string password)
    {
        if (blob.Length < MagicLength + SaltSize + NonceSize + TagSize)
            throw new InvalidDataException("Export file is truncated or not a Historia encrypted bundle.");

        var span = blob.AsSpan();
        int version;
        if (span[..MagicLength].SequenceEqual(MagicV1))
            version = 1;
        else if (span[..MagicLength].SequenceEqual(MagicV2))
            version = 2;
        else
            throw new InvalidDataException("Not a Historia encrypted export bundle.");

        var offset = MagicLength;
        var salt = span.Slice(offset, SaltSize).ToArray(); offset += SaltSize;
        var nonce = span.Slice(offset, NonceSize).ToArray(); offset += NonceSize;
        var tag = span.Slice(offset, TagSize).ToArray(); offset += TagSize;
        var ciphertext = span[offset..].ToArray();

        var key = DeriveKey(password, salt, version);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException("Incorrect password or corrupted export file.");
        }

        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] DeriveKey(string password, byte[] salt, int version) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, IterationsFor(version), HashAlgorithmName.SHA256, KeySize);
}
