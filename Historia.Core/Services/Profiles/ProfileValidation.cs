namespace Historia.Services.Profiles;

/// <summary>Profile field validation rules (§9.2). Throws <see cref="ProfileValidationException"/> on failure.</summary>
public static partial class ProfileValidation
{
    public const int MaxProfiles = 50;

    public static void ValidateNew(
        string displayName, string userName, string userEmail,
        string? sshKeyPath, string? gpgKeyId,
        IReadOnlyList<GitProfile> existing)
    {
        if (existing.Count >= MaxProfiles)
            throw new ProfileValidationException($"The maximum of {MaxProfiles} profiles has been reached.");

        ValidateFields(displayName, userName, userEmail, sshKeyPath, gpgKeyId);

        if (existing.Any(p => string.Equals(p.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)))
            throw new ProfileValidationException($"A profile named '{displayName}' already exists.");
    }

    public static void ValidateUpdate(
        Guid id, string displayName, string userName, string userEmail,
        string? sshKeyPath, string? gpgKeyId,
        IReadOnlyList<GitProfile> existing)
    {
        ValidateFields(displayName, userName, userEmail, sshKeyPath, gpgKeyId);

        if (existing.Any(p => p.Id != id &&
                              string.Equals(p.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)))
            throw new ProfileValidationException($"A profile named '{displayName}' already exists.");
    }

    private static void ValidateFields(
        string displayName, string userName, string userEmail, string? sshKeyPath, string? gpgKeyId)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 100)
            throw new ProfileValidationException("Display name is required and must be 1–100 characters.");

        if (string.IsNullOrWhiteSpace(userName) || userName.Length > 128)
            throw new ProfileValidationException("Git user name is required and must be 1–128 characters.");

        if (string.IsNullOrWhiteSpace(userEmail) || !EmailRegex().IsMatch(userEmail))
            throw new ProfileValidationException("A valid email address is required.");

        if (!string.IsNullOrWhiteSpace(sshKeyPath) && !File.Exists(sshKeyPath))
            throw new ProfileValidationException($"SSH key file not found: {sshKeyPath}");

        if (!string.IsNullOrWhiteSpace(gpgKeyId) && !GpgKeyRegex().IsMatch(gpgKeyId))
            throw new ProfileValidationException("GPG key id must be 8–40 hexadecimal characters.");
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^[0-9A-Fa-f]{8,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex GpgKeyRegex();
}

public sealed class ProfileValidationException(string message) : Exception(message);
