namespace Historia.Tests;

[Trait("tier", "unit")]
public class AiModelCatalogTests
{
    private sealed class FakeClient : IAiChatClient
    {
        public Func<AiChatRequest, IReadOnlyList<string>> ListScript { get; set; } = _ => ["model-a", "model-b"];

        public Task<IReadOnlyList<string>> ListModelsAsync(AiChatRequest request, CancellationToken ct) =>
            Task.FromResult(ListScript(request));

        public Task<string> CompleteAsync(AiChatRequest request, CancellationToken ct) =>
            Task.FromResult("reply");
    }

    private sealed class FakeFactory(FakeClient client) : IAiClientFactory
    {
        public IAiChatClient For(AiServiceKind kind) => client;
    }

    private static (AiModelCatalog Catalog, AiServiceProfile Profile, FakeClient Client) CreateRig()
    {
        var settings = new FakeSettingsService();
        var profiles = new AiProfileService(settings);
        var client = new FakeClient();
        var factory = new FakeFactory(client);
        var catalog = new AiModelCatalog(profiles, factory);
        var profile = new AiServiceProfile
        {
            Id = "p1",
            Name = "Custom",
            Kind = AiServiceKind.Custom,
            Server = "https://ai.example.test/v1",
            ApiKey = "key1",
            ReadApiKeyFromEnv = false,
            Model = "model-a",
            ReasoningEffort = "medium",
            ExtraHeaders = "X-Test: 1",
        };
        return (catalog, profile, client);
    }

    [Fact]
    public async Task Get_is_null_until_a_list_is_loaded()
    {
        var (catalog, profile, _) = CreateRig();
        Assert.Null(catalog.Get(profile));

        await catalog.LoadAsync(profile);
        Assert.NotNull(catalog.Get(profile));
    }

    [Fact]
    public async Task LoadAsync_remembers_the_list_and_raises_Changed_with_the_id()
    {
        var (catalog, profile, _) = CreateRig();
        string? changedId = null;
        catalog.Changed += (_, id) => changedId = id;

        var loaded = await catalog.LoadAsync(profile);

        Assert.Equal(profile.Id, changedId);
        Assert.Equal(loaded, catalog.Get(profile));
    }

    [Fact]
    public async Task Changing_server_kind_key_or_headers_hides_the_cached_list()
    {
        var (catalog, profile, _) = CreateRig();
        await catalog.LoadAsync(profile);

        Assert.Null(catalog.Get(profile with { Server = "https://other.test" }));
        Assert.Null(catalog.Get(profile with { Kind = AiServiceKind.Gemini }));
        Assert.Null(catalog.Get(profile with { ApiKey = "different-key" }));
        Assert.Null(catalog.Get(profile with { ReadApiKeyFromEnv = true }));
        Assert.Null(catalog.Get(profile with { ExtraHeaders = "X-Other: 2" }));
    }

    [Fact]
    public async Task Changing_model_name_or_effort_keeps_the_cached_list()
    {
        var (catalog, profile, _) = CreateRig();
        var loaded = await catalog.LoadAsync(profile);

        Assert.Equal(loaded, catalog.Get(profile with { Model = "other-model" }));
        Assert.Equal(loaded, catalog.Get(profile with { Name = "renamed" }));
        Assert.Equal(loaded, catalog.Get(profile with { ReasoningEffort = "high" }));
    }

    [Fact]
    public async Task A_failed_load_remembers_nothing_and_raises_nothing()
    {
        var (catalog, profile, client) = CreateRig();
        client.ListScript = _ => throw new AiException(AiErrorKind.Network, "offline");
        var changedFired = false;
        catalog.Changed += (_, _) => changedFired = true;

        await Assert.ThrowsAsync<AiException>(() => catalog.LoadAsync(profile));

        Assert.False(changedFired);
        Assert.Null(catalog.Get(profile));
    }

    [Fact]
    public void Remember_stores_and_raises_Changed()
    {
        var (catalog, profile, _) = CreateRig();
        string? changedId = null;
        catalog.Changed += (_, id) => changedId = id;

        catalog.Remember(profile, ["remembered-1", "remembered-2"]);

        Assert.Equal(profile.Id, changedId);
        Assert.Equal(["remembered-1", "remembered-2"], catalog.Get(profile));
    }

    [Fact]
    public async Task Changed_is_raised_outside_the_lock()
    {
        var (catalog, profile, _) = CreateRig();
        IReadOnlyList<string>? backgroundResult = null;
        var backgroundRan = false;

        catalog.Changed += (_, _) =>
        {
            var task = Task.Run(() => catalog.Get(profile));
            Assert.True(task.Wait(TimeSpan.FromSeconds(5)), "Background Get timed out waiting for catalog lock.");
            backgroundResult = task.Result;
            backgroundRan = true;
        };

        await catalog.LoadAsync(profile);

        Assert.True(backgroundRan);
        Assert.NotNull(backgroundResult);
    }
}
