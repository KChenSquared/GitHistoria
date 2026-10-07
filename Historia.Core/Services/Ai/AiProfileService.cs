namespace Historia.Services.Ai;

/// <inheritdoc cref="IAiProfileService"/>
public sealed class AiProfileService : IAiProfileService, IDisposable
{
    private readonly ISettingsService _settings;

    public event EventHandler? Changed;

    public AiProfileService(ISettingsService settings)
    {
        _settings = settings;
        _settings.Changed += OnSettingsChanged;
    }

    public IReadOnlyList<AiServiceProfile> Profiles => _settings.Current.AiServices;

    public AiServiceProfile? ActiveProfile
    {
        get
        {
            var current = _settings.Current;
            var id = current.ActiveAiServiceId;
            return id is null ? null : current.AiServices.FirstOrDefault(p => p.Id == id);
        }
    }

    public string DisplayLabel(AiServiceProfile profile)
    {
        var index = 0;
        var list = Profiles;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Id == profile.Id)
            {
                index = i + 1;
                break;
            }
        }
        var name = string.IsNullOrWhiteSpace(profile.Name) ? AiServiceTemplates.DefaultName : profile.Name;
        return $"{index}. {name}";
    }

    public async Task<AiServiceProfile> AddAsync(CancellationToken ct = default)
    {
        var profile = AiServiceTemplates.NewCustom(DateTime.UtcNow);
        await _settings.UpdateAsync(s => s with { AiServices = [.. s.AiServices, profile] }, ct).ConfigureAwait(false);
        return profile;
    }

    public Task UpdateAsync(string id, Func<AiServiceProfile, AiServiceProfile> mutate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        return _settings.UpdateAsync(s =>
        {
            var index = IndexOf(s.AiServices, id);
            if (index < 0)
                return s;
            var updated = mutate(s.AiServices[index]) with { Id = id };
            if (updated == s.AiServices[index])
                return s;
            var list = s.AiServices.ToList();
            list[index] = updated;
            return s with { AiServices = list };
        }, ct);
    }

    public Task DeleteAiServiceAsync(string id, CancellationToken ct = default) =>
        _settings.UpdateAsync(s =>
        {
            var index = IndexOf(s.AiServices, id);
            if (index < 0)
                return s;
            var list = s.AiServices.ToList();
            list.RemoveAt(index);
            return s with
            {
                AiServices = list,
                ActiveAiServiceId = s.ActiveAiServiceId == id ? null : s.ActiveAiServiceId,
            };
        }, ct);

    public Task SetActiveAsync(string? id, CancellationToken ct = default) =>
        _settings.UpdateAsync(s =>
        {
            var valid = id is not null && IndexOf(s.AiServices, id) >= 0;
            return s with { ActiveAiServiceId = valid ? id : null };
        }, ct);

    public string? ResolveApiKey(AiServiceProfile profile)
    {
        var value = profile.ReadApiKeyFromEnv
            ? string.IsNullOrWhiteSpace(profile.ApiKey) ? null : Environment.GetEnvironmentVariable(profile.ApiKey.Trim())
            : profile.ApiKey;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public bool HasApiKey(AiServiceProfile profile) => ResolveApiKey(profile) is not null;

    public void Dispose() => _settings.Changed -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OldSettings.AiServices, e.NewSettings.AiServices)
            || e.OldSettings.ActiveAiServiceId != e.NewSettings.ActiveAiServiceId)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private static int IndexOf(IReadOnlyList<AiServiceProfile> list, string id)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i].Id == id)
                return i;
        return -1;
    }
}
