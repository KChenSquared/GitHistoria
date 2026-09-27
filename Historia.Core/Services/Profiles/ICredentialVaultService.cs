namespace Historia.Services.Profiles;

/// <summary>
/// Secure credential storage via Windows Credential Manager (DPAPI-backed). All methods
/// are async to avoid blocking the UI thread during vault access, which can be slow on first
/// call or while Defender scans the vault. Registered as a singleton.
/// </summary>
public interface ICredentialVaultService
{
    /// <summary>Stores a secret under <c>Historia/&lt;key&gt;</c>, overwriting any existing value.</summary>
    Task StoreTokenAsync(string key, string token, CancellationToken ct = default);

    /// <summary>Returns the secret for <paramref name="key"/>, or null when absent.</summary>
    Task<string?> RetrieveTokenAsync(string key, CancellationToken ct = default);

    /// <summary>Deletes the secret for <paramref name="key"/>. A missing key is not an error.</summary>
    Task DeleteTokenAsync(string key, CancellationToken ct = default);
}
