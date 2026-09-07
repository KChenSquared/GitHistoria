
namespace Historia.Services.Profiles;

public sealed record ActiveProfileChangedEventArgs(GitProfile? Profile, RepoIdentityKey? RepoKey);

/// <summary>
/// Core multi-account profile management (§9.8). All I/O methods are async with
/// <see cref="CancellationToken"/> support. Registered as a singleton.
/// </summary>
public interface IProfileService
{
    // ── Profile CRUD ──
    IReadOnlyList<GitProfile> GetAllProfiles();
    GitProfile? GetProfileById(Guid id);
    ProfileUsageState? GetUsage(Guid id);
    Task<GitProfile> CreateProfileAsync(GitProfileCreateRequest request, CancellationToken ct = default);
    Task<GitProfile> UpdateProfileAsync(Guid id, GitProfileUpdateRequest request, CancellationToken ct = default);
    Task<ProfileDeleteResult> DeleteProfileAsync(Guid id, CancellationToken ct = default);

    // ── Global profile (§14.1) ──
    GitProfile GetGlobalProfile();
    Task SyncGlobalIdentityAsync(CancellationToken ct = default);

    // ── Default profile (§14.2) ──
    GitProfile? GetDefaultProfile();
    Task SetDefaultProfileAsync(Guid? profileId, CancellationToken ct = default);

    // ── Per-repo selection (§14.3) ──
    GitProfile? GetProfileForRepository(RepoIdentityKey repoKey);
    bool IsLocalConfigSelected(RepoIdentityKey repoKey);
    Task BindProfileToRepositoryAsync(RepoIdentityKey repoKey, string displayPath, Guid profileId, CancellationToken ct = default);
    Task SetLocalConfigForRepositoryAsync(RepoIdentityKey repoKey, string displayPath, CancellationToken ct = default);
    Task UnbindRepositoryAsync(RepoIdentityKey repoKey, CancellationToken ct = default);
    IReadOnlyDictionary<RepoIdentityKey, RepoProfileBinding> GetAllRepoBindings();
    IReadOnlyList<RepoProfileBinding> GetStaleBindings();
    Task<int> CleanStaleBindingsAsync(CancellationToken ct = default);

    /// <summary>Applies the cascading resolution of §14.3 for the given repo.</summary>
    GitProfile? ResolveActiveProfile(RepoIdentityKey? repoKey = null);

    // ── Application ──
    Task ApplyProfileToGitConfigAsync(GitProfile profile, string repoPath, GitConfigScope scope, CancellationToken ct = default);
    Task TouchProfileAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>Retrieves the active token for a profile/host from the credential vault.</summary>
    Task<string?> GetTokenAsync(Guid profileId, RemoteHost host, CancellationToken ct = default);

    // ── Export / import ──
    Task ExportProfilesAsync(string exportFilePath, bool includeTokens, string? password, CancellationToken ct = default);
    Task ImportProfilesAsync(string importFilePath, string? password, CancellationToken ct = default);

    // ── Events ──
    event EventHandler? ProfilesChanged;
    event EventHandler<ActiveProfileChangedEventArgs>? ActiveProfileChanged;
}
