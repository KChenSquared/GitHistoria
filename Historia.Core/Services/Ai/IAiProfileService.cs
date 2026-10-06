namespace Historia.Services.Ai;

/// <summary>
/// The AI integrations and which one is active, over <see cref="ISettingsService"/>. Singleton.
/// <see cref="Changed"/> is raised on the thread that made the change; UI subscribers post to
/// the UI thread. Callers confirm destructive actions; nothing here prompts.
/// </summary>
public interface IAiProfileService
{
    /// <summary>Every profile, in the order it was added.</summary>
    IReadOnlyList<AiServiceProfile> Profiles { get; }

    /// <summary>The active profile, or null when none is active or the stored id matches no profile.</summary>
    AiServiceProfile? ActiveProfile { get; }

    /// <summary><c>1. Name</c>, numbered by list position, so it renumbers after a delete.</summary>
    string DisplayLabel(AiServiceProfile profile);

    /// <summary>Appends a blank Custom profile.</summary>
    Task<AiServiceProfile> AddAsync(CancellationToken ct = default);

    /// <summary>Applies <paramref name="mutate"/> to the profile with that id. The id itself is kept.
    /// A missing id is a no-op.</summary>
    Task UpdateAsync(string id, Func<AiServiceProfile, AiServiceProfile> mutate, CancellationToken ct = default);

    /// <summary>Removes the profile and clears the active id if it matched.</summary>
    Task DeleteAiServiceAsync(string id, CancellationToken ct = default);

    /// <summary>Makes <paramref name="id"/> the only active profile; null (or an unknown id) turns AI off.</summary>
    Task SetActiveAsync(string? id, CancellationToken ct = default);

    /// <summary>The key to send: <see cref="AiServiceProfile.ApiKey"/>, or the named environment
    /// variable's value in environment mode. Null when blank or unset.</summary>
    string? ResolveApiKey(AiServiceProfile profile);

    bool HasApiKey(AiServiceProfile profile);

    /// <summary>Raised when the profiles or the active id change.</summary>
    event EventHandler? Changed;
}
