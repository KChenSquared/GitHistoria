namespace Historia.Tests;

[Trait("tier", "unit")]
public class AiSettingsPersistenceTests : IDisposable
{
    private readonly StateDir _state = new();

    public void Dispose() => _state.Dispose();

    [Fact]
    public async Task Profiles_and_active_id_round_trip_through_the_settings_file()
    {
        var first = new AiServiceProfile
        {
            Name = "Gemini",
            Kind = AiServiceKind.Gemini,
            Server = AiServiceTemplates.GeminiServer,
            Model = "gemini-2.5-flash",
            ApiKey = "secret-one",
            ReasoningEffort = "low",
            AdditionalPrompt = "- keep it short",
            ExtraHeaders = "X-Team: core",
            DiffCapUnits = 2,
            CreatedUtc = new DateTime(2026, 10, 6, 1, 0, 0, DateTimeKind.Utc),
        };
        var second = new AiServiceProfile
        {
            Name = "Work",
            Server = "https://example.test/v1",
            ReadApiKeyFromEnv = true,
            ApiKey = "WORK_KEY_VAR",
            AutoFetchModels = false,
        };

        var svc = SettingsService.Load();
        await svc.UpdateAsync(s => s with { AiServices = [first, second], ActiveAiServiceId = second.Id });
        await svc.FlushAsync();

        var reloaded = SettingsService.Load();
        Assert.Equal([first, second], reloaded.Current.AiServices);
        Assert.Equal(second.Id, reloaded.Current.ActiveAiServiceId);
    }

    [Fact]
    public void An_old_settings_file_without_AI_fields_loads_empty()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"schemaVersion\": 22, \"appTheme\": \"dark\" }");

        var svc = SettingsService.Load();

        Assert.Empty(svc.Current.AiServices);
        Assert.Null(svc.Current.ActiveAiServiceId);
    }
}
