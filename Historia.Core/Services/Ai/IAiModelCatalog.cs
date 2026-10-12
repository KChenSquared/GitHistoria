namespace Historia.Services.Ai;

/// <summary>
/// Caches the model ids last fetched for each AI integration profile.
/// </summary>
public interface IAiModelCatalog
{
    /// <summary>The ids last loaded for this profile's connection, or null when none are loaded or the connection changed since.</summary>
    IReadOnlyList<string>? Get(AiServiceProfile profile);

    /// <summary>Asks the service for its models, remembers the result, and raises <see cref="Changed"/>.</summary>
    Task<IReadOnlyList<string>> LoadAsync(AiServiceProfile profile, CancellationToken ct = default);

    /// <summary>Records a list loaded elsewhere and raises <see cref="Changed"/>.</summary>
    void Remember(AiServiceProfile profile, IReadOnlyList<string> ids);

    /// <summary>Claims this profile's automatic model switch. False when a switch was claimed for it
    /// within the last five minutes, so an id the user chose is not overwritten again and again.</summary>
    bool TryClaimAutoSwitch(string profileId);

    /// <summary>Raised on the thread that finished the load; UI subscribers post to the UI thread. The argument is the profile id.</summary>
    event EventHandler<string>? Changed;
}
