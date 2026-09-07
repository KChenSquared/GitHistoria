namespace Historia.Models;

/// <summary>Fields for creating a profile (§9.8). Tokens are supplied separately and stored in the vault.</summary>
public sealed record GitProfileCreateRequest
{
    public required string DisplayName { get; init; }
    public required string GitUserName { get; init; }
    public required string GitUserEmail { get; init; }
    public string? AvatarPath { get; init; }
    public string? SshKeyPath { get; init; }
    public string? GpgSigningKeyId { get; init; }
    public bool SignCommits { get; init; }

    /// <summary>Plaintext PATs to store in Windows Credential Manager (never persisted to JSON).</summary>
    public string? GitHubToken { get; init; }
    public string? GitLabToken { get; init; }
    public string? BitbucketToken { get; init; }
}

/// <summary>Fields for updating a profile (§9.8). A null field leaves the existing value unchanged.</summary>
public sealed record GitProfileUpdateRequest
{
    public string? DisplayName { get; init; }
    public string? GitUserName { get; init; }
    public string? GitUserEmail { get; init; }
    public string? AvatarPath { get; init; }
    public string? SshKeyPath { get; init; }
    public string? GpgSigningKeyId { get; init; }
    public bool? SignCommits { get; init; }

    /// <summary>When set, replaces the stored token. Empty string clears it.</summary>
    public string? GitHubToken { get; init; }
    public string? GitLabToken { get; init; }
    public string? BitbucketToken { get; init; }
}

