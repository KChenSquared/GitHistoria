namespace Historia.Tests;

[Trait("tier", "unit")]
public class AiProfileServiceTests
{
    private static (AiProfileService Service, FakeSettingsService Settings) Create()
    {
        var settings = new FakeSettingsService();
        return (new AiProfileService(settings), settings);
    }

    [Fact]
    public async Task Add_appends_and_numbers_in_list_order()
    {
        var (svc, _) = Create();
        var a = await svc.AddAsync();
        var b = await svc.AddAsync();
        await svc.UpdateAsync(a.Id, p => p with { Name = "Gemini" });
        await svc.UpdateAsync(b.Id, p => p with { Name = "Work" });

        var labels = svc.Profiles.Select(svc.DisplayLabel).ToList();

        Assert.Equal(["1. Gemini", "2. Work"], labels);
    }

    [Fact]
    public async Task Delete_renumbers_the_remaining_profiles()
    {
        var (svc, _) = Create();
        var a = await svc.AddAsync();
        var b = await svc.AddAsync();
        await svc.UpdateAsync(b.Id, p => p with { Name = "Work" });

        await svc.DeleteAiServiceAsync(a.Id);

        Assert.Equal(["1. Work"], svc.Profiles.Select(svc.DisplayLabel).ToList());
    }

    [Fact]
    public async Task Deleting_the_active_profile_clears_active()
    {
        var (svc, settings) = Create();
        var a = await svc.AddAsync();
        await svc.SetActiveAsync(a.Id);

        await svc.DeleteAiServiceAsync(a.Id);

        Assert.Null(svc.ActiveProfile);
        Assert.Null(settings.Current.ActiveAiServiceId);
    }

    [Fact]
    public async Task Deleting_another_profile_keeps_the_active_one()
    {
        var (svc, _) = Create();
        var a = await svc.AddAsync();
        var b = await svc.AddAsync();
        await svc.SetActiveAsync(a.Id);

        await svc.DeleteAiServiceAsync(b.Id);

        Assert.Equal(a.Id, svc.ActiveProfile?.Id);
    }

    [Fact]
    public async Task Only_one_profile_is_active_at_a_time()
    {
        var (svc, _) = Create();
        var a = await svc.AddAsync();
        var b = await svc.AddAsync();

        await svc.SetActiveAsync(a.Id);
        await svc.SetActiveAsync(b.Id);

        Assert.Equal(b.Id, svc.ActiveProfile?.Id);
        await svc.SetActiveAsync(null);
        Assert.Null(svc.ActiveProfile);
    }

    [Fact]
    public async Task A_dangling_active_id_reads_as_no_active_profile()
    {
        var (svc, settings) = Create();
        await settings.UpdateAsync(s => s with { ActiveAiServiceId = "missing" });

        Assert.Null(svc.ActiveProfile);
    }

    [Fact]
    public async Task Setting_an_unknown_id_active_turns_AI_off()
    {
        var (svc, settings) = Create();
        var a = await svc.AddAsync();
        await svc.SetActiveAsync(a.Id);

        await svc.SetActiveAsync("missing");

        Assert.Null(settings.Current.ActiveAiServiceId);
    }

    [Fact]
    public async Task Update_keeps_the_id_even_if_the_mutation_changes_it()
    {
        var (svc, _) = Create();
        var a = await svc.AddAsync();

        await svc.UpdateAsync(a.Id, p => p with { Id = "other", Model = "m" });

        Assert.Equal(a.Id, svc.Profiles.Single().Id);
        Assert.Equal("m", svc.Profiles.Single().Model);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("sk-123", true)]
    public void HasApiKey_for_a_literal_key(string key, bool expected)
    {
        var (svc, _) = Create();
        Assert.Equal(expected, svc.HasApiKey(new AiServiceProfile { ApiKey = key }));
    }

    [Fact]
    public void Env_mode_resolves_the_named_variable()
    {
        var (svc, _) = Create();
        var name = $"HISTORIA_TEST_KEY_{Guid.NewGuid():N}";
        var p = new AiServiceProfile { ApiKey = name, ReadApiKeyFromEnv = true };
        try
        {
            Assert.False(svc.HasApiKey(p));

            Environment.SetEnvironmentVariable(name, "   ");
            Assert.False(svc.HasApiKey(p));

            Environment.SetEnvironmentVariable(name, "from-env");
            Assert.Equal("from-env", svc.ResolveApiKey(p));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task Changed_fires_for_AI_changes_and_not_for_unrelated_settings()
    {
        var (svc, settings) = Create();
        var raised = 0;
        svc.Changed += (_, _) => raised++;

        await settings.UpdateAsync(s => s with { DiffContextLines = 9 });
        Assert.Equal(0, raised);

        var a = await svc.AddAsync();
        Assert.Equal(1, raised);

        await svc.SetActiveAsync(a.Id);
        Assert.Equal(2, raised);

        await svc.UpdateAsync(a.Id, p => p with { Name = "x" });
        Assert.Equal(3, raised);
    }

    [Fact]
    public async Task Dispose_unsubscribes_from_settings()
    {
        var (svc, settings) = Create();
        var raised = 0;
        svc.Changed += (_, _) => raised++;

        svc.Dispose();
        await settings.UpdateAsync(s => s with { ActiveAiServiceId = "x" });

        Assert.Equal(0, raised);
    }
}
