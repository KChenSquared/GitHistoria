namespace Historia.Models;

/// <summary>
/// Immutable, user-editable Git identity profile (§9.2). All mutations produce a new instance
/// via a <c>with</c> expression. Secrets are never stored inline — OAuth PATs live in Windows
/// Credential Manager and are referenced here only by key.
/// </summary>
public sealed record GitProfile
{
    public required Guid Id { get; init; }

    /// <summary>e.g. "Personal", "MegaCorp Work". Required, 1–100 chars, unique across profiles.</summary>
    public required string DisplayName { get; init; }

    /// <summary><c>git config user.name</c>. Required, 1–128 chars.</summary>
    public required string GitUserName { get; init; }

    /// <summary><c>git config user.email</c>. Required, valid email format. May be duplicated across profiles.</summary>
    public required string GitUserEmail { get; init; }

    /// <summary>
    /// True for the single reserved global profile (§14.1). Its identity mirrors
    /// <c>~/.gitconfig</c> and re-syncs each launch; it cannot be deleted, sorts first in every
    /// picker, and carries no SSH key / PAT of its own.
    /// </summary>
    public bool IsGlobal { get; init; }

    // ── Optional fields ──
    public string? AvatarPath { get; init; }
    public string? SshKeyPath { get; init; }
    public string? GpgSigningKeyId { get; init; }
    public bool SignCommits { get; init; }

    // ── OAuth PAT references (actual tokens in Windows Credential Manager) ──
    public string? GitHubTokenRef { get; init; }
    public string? GitLabTokenRef { get; init; }
    public string? BitbucketTokenRef { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Mutable usage state tracked separately from the immutable profile config so record
/// semantics are preserved while timestamps update (§9.2). Updated by
/// <c>IProfileService.TouchProfileAsync</c> when a profile becomes the active resolved
/// profile for any repository.
/// </summary>
public sealed class ProfileUsageState
{
    public required Guid ProfileId { get; init; }
    public DateTimeOffset LastUsedAt { get; set; }
    public long UseCount { get; set; }
}

/// <summary>
/// Repository → profile binding entry (§9.3.1). Keyed in <c>repo-profiles.json</c> by
/// <c>RepoIdentityKey</c> (root-commit derived), never by path. The last-known path is stored
/// only for display and stale-binding detection.
/// </summary>
public sealed record RepoProfileBinding
{
    public required Guid ProfileId { get; init; }
    public required string LastKnownPath { get; init; }
    public required DateTimeOffset BoundAt { get; init; }
}
