
namespace Historia.Services.Profiles;

/// <summary>
/// Core multi-account profile management. All I/O methods are async with
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

    // ── Global profile ──
    GitProfile GetGlobalProfile();
    Task SyncGlobalIdentityAsync(CancellationToken ct = default);

    // ── Default profile ──
    GitProfile? GetDefaultProfile();
    Task SetDefaultProfileAsync(Guid? profileId, CancellationToken ct = default);

    // ── Per-repo selection ──
    GitProfile? GetProfileForRepository(RepoIdentityKey repoKey);
    bool IsLocalConfigSelected(RepoIdentityKey repoKey);
    Task BindProfileToRepositoryAsync(RepoIdentityKey repoKey, string displayPath, Guid profileId, CancellationToken ct = default);
    Task SetLocalConfigForRepositoryAsync(RepoIdentityKey repoKey, string displayPath, CancellationToken ct = default);
    Task UnbindRepositoryAsync(RepoIdentityKey repoKey, CancellationToken ct = default);
    IReadOnlyDictionary<RepoIdentityKey, RepoProfileBinding> GetAllRepoBindings();
    IReadOnlyList<RepoProfileBinding> GetStaleBindings();
    Task<int> CleanStaleBindingsAsync(CancellationToken ct = default);

    /// <summary>Resolves the active profile for <paramref name="repoKey"/>: <c>null</c> when the
    /// repo is bound to its local config, else the bound profile if it still exists, else the
    /// default profile when it is not global; <c>null</c> otherwise.</summary>
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
}
