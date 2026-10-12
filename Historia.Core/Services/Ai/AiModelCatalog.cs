namespace Historia.Services.Ai;

/// <inheritdoc cref="IAiModelCatalog"/>
public sealed class AiModelCatalog(IAiProfileService profiles, IAiClientFactory clients) : IAiModelCatalog
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _cache = [];

    private sealed record Fingerprint(
        AiServiceKind Kind,
        string Server,
        string? ApiKey,
        bool ReadApiKeyFromEnv,
        string ExtraHeaders);

    private sealed record Entry(Fingerprint Fingerprint, IReadOnlyList<string> Ids);

    public event EventHandler<string>? Changed;

    public IReadOnlyList<string>? Get(AiServiceProfile profile)
    {
        var fp = FingerprintOf(profile);
        lock (_lock)
        {
            if (_cache.TryGetValue(profile.Id, out var entry) && entry.Fingerprint == fp)
                return entry.Ids;
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> LoadAsync(AiServiceProfile profile, CancellationToken ct = default)
    {
        var client = clients.For(profile.Kind);
        var request = new AiChatRequest(
            profile.Server,
            profiles.ResolveApiKey(profile),
            profile.Model,
            profile.ReasoningEffort,
            profile.ExtraHeaders,
            "");

        var ids = await Task.Run(() => client.ListModelsAsync(request, ct), ct).ConfigureAwait(false);

        var fp = FingerprintOf(profile);
        lock (_lock)
        {
            _cache[profile.Id] = new Entry(fp, ids);
        }

        Changed?.Invoke(this, profile.Id);
        return ids;
    }

    public void Remember(AiServiceProfile profile, IReadOnlyList<string> ids)
    {
        var fp = FingerprintOf(profile);
        lock (_lock)
        {
            _cache[profile.Id] = new Entry(fp, ids);
        }

        Changed?.Invoke(this, profile.Id);
    }

    private static Fingerprint FingerprintOf(AiServiceProfile profile) =>
        new(profile.Kind, profile.Server, profile.ApiKey, profile.ReadApiKeyFromEnv, profile.ExtraHeaders);
}
