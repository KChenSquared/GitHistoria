namespace Historia.Tests;
[Trait("tier", "unit")]

public class ProfileServiceTests : IDisposable
{
    private readonly StateDir _state = new();
    private readonly FakeVault _vault = new();
    private readonly SettingsService _settings;
    private readonly ProfileService _profiles;

    public ProfileServiceTests()
    {
        _settings = SettingsService.Load();
        _profiles = new ProfileService(_settings, _vault, new NoGitCli());
    }

    private Task<GitProfile> Create(string name, string email = "u@example.com") =>
        _profiles.CreateProfileAsync(new GitProfileCreateRequest
        {
            DisplayName = name,
            GitUserName = name,
            GitUserEmail = email,
        });

    [Fact]
    public void Global_profile_is_synthesized_and_sorts_first()
    {
        var all = _profiles.GetAllProfiles();
        Assert.Single(all);
        Assert.True(all[0].IsGlobal);
        Assert.Same(all[0], _profiles.GetGlobalProfile());
    }

    [Fact]
    public async Task Global_is_the_default_and_opened_repos_resolve_to_local_config()
    {
        // Default (null → global) means an opened repo with no row → null (Local config).
        Assert.Null(_settings.Current.DefaultProfileId);
        Assert.Null(_profiles.ResolveActiveProfile());

        await Create("Personal"); // creating a profile does not change the default
        Assert.Null(_settings.Current.DefaultProfileId);
        Assert.Null(_profiles.ResolveActiveProfile());
    }

    [Fact]
    public async Task Duplicate_display_name_is_rejected()
    {
        await Create("Work");
        await Assert.ThrowsAsync<ProfileValidationException>(() => Create("work"));
    }

    [Fact]
    public async Task Invalid_email_is_rejected()
        => await Assert.ThrowsAsync<ProfileValidationException>(() => Create("Bad", "not-an-email"));

    [Fact]
    public async Task Resolution_cascade_follows_section_14_3()
    {
        var def = await Create("Default");
        var bound = await Create("Bound");
        var key = RepoIdentityKey.FromRootCommit("abc123abc123abc123abc123abc123abc123abcd");

        // (3) no row + global default → null (Local config).
        Assert.Null(_profiles.ResolveActiveProfile(key));

        // (2) no row + non-global default → that default.
        await _profiles.SetDefaultProfileAsync(def.Id);
        Assert.Equal(def.Id, _profiles.ResolveActiveProfile(key)!.Id);

        // (1) explicit row → the bound profile.
        await _profiles.BindProfileToRepositoryAsync(key, @"C:\repo", bound.Id);
        Assert.Equal(bound.Id, _profiles.ResolveActiveProfile(key)!.Id);

        // (1) explicit Local-config marker → null even when the default is non-global.
        await _profiles.SetLocalConfigForRepositoryAsync(key, @"C:\repo");
        Assert.Null(_profiles.ResolveActiveProfile(key));
        Assert.True(_profiles.IsLocalConfigSelected(key));

        // A row naming a deleted profile is ignored → falls through to the default.
        await _profiles.BindProfileToRepositoryAsync(key, @"C:\repo", bound.Id);
        await _profiles.DeleteProfileAsync(bound.Id);
        Assert.Equal(def.Id, _profiles.ResolveActiveProfile(key)!.Id);
    }

    [Fact]
    public async Task The_global_profile_cannot_be_deleted()
    {
        var result = await _profiles.DeleteProfileAsync(_profiles.GetGlobalProfile().Id);
        Assert.Equal(ProfileDeleteResult.CannotDeleteGlobal, result);
        Assert.Single(_profiles.GetAllProfiles());
    }

    [Fact]
    public async Task Deleting_the_default_profile_clears_the_default()
    {
        var first = await Create("First");
        await Create("Second");
        await _profiles.SetDefaultProfileAsync(first.Id);

        var result = await _profiles.DeleteProfileAsync(first.Id);

        Assert.Equal(ProfileDeleteResult.DeletedClearedDefault, result);
        Assert.Null(_settings.Current.DefaultProfileId);
        Assert.True(_profiles.GetDefaultProfile()!.IsGlobal);
    }

    [Fact]
    public async Task Deleting_a_non_default_profile_leaves_the_default_alone()
    {
        var def = await Create("Default");
        var other = await Create("Other");
        await _profiles.SetDefaultProfileAsync(def.Id);

        var result = await _profiles.DeleteProfileAsync(other.Id);

        Assert.Equal(ProfileDeleteResult.Deleted, result);
        Assert.Equal(def.Id, _settings.Current.DefaultProfileId);
    }

    [Fact]
    public async Task Deleting_a_profile_removes_its_bindings_and_tokens()
    {
        var keep = await Create("Keep");
        var doomed = await _profiles.CreateProfileAsync(new GitProfileCreateRequest
        {
            DisplayName = "Doomed",
            GitUserName = "Doomed",
            GitUserEmail = "d@example.com",
            GitHubToken = "ghp_secret",
        });
        var key = RepoIdentityKey.FromRootCommit("f00df00df00df00df00df00df00df00df00df00d0");
        await _profiles.BindProfileToRepositoryAsync(key, @"C:\repo", doomed.Id);
        Assert.Equal(1, _vault.Count);

        await _profiles.DeleteProfileAsync(doomed.Id);

        Assert.Null(_profiles.GetProfileForRepository(key));
        Assert.Equal(0, _vault.Count);
        // global + keep
        Assert.Equal(2, _profiles.GetAllProfiles().Count);
        Assert.Contains(_profiles.GetAllProfiles(), p => p.Id == keep.Id);
    }

    [Fact]
    public async Task Token_is_stored_in_the_vault_not_in_the_profile_record()
    {
        var p = await _profiles.CreateProfileAsync(new GitProfileCreateRequest
        {
            DisplayName = "Tok",
            GitUserName = "Tok",
            GitUserEmail = "t@example.com",
            GitHubToken = "ghp_abc123",
        });

        Assert.NotNull(p.GitHubTokenRef);
        Assert.DoesNotContain("ghp_abc123", p.GitHubTokenRef);
        Assert.Equal("ghp_abc123", await _profiles.GetTokenAsync(p.Id, RemoteHost.GitHub));
    }

    // ── Profile edits run one at a time and persist newest-last ──

    /// <summary>A vault whose first store can be held mid-call, standing in for a slow Credential
    /// Manager write.</summary>
    private sealed class GatedVault : ICredentialVaultService
    {
        private readonly ConcurrentDictionary<string, string> _store = new();
        public bool Hold { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count => _store.Count;

        public async Task StoreTokenAsync(string key, string token, CancellationToken ct = default)
        {
            if (Hold)
            {
                Entered.TrySetResult();
                await Release.Task.ConfigureAwait(false);
            }
            _store[key] = token;
        }

        public Task<string?> RetrieveTokenAsync(string key, CancellationToken ct = default)
            => Task.FromResult(_store.GetValueOrDefault(key));

        public Task DeleteTokenAsync(string key, CancellationToken ct = default)
        {
            _store.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Update_racing_delete_leaves_no_token_in_the_vault()
    {
        var vault = new GatedVault();
        var profiles = new ProfileService(_settings, vault, new NoGitCli());
        var p = await profiles.CreateProfileAsync(new GitProfileCreateRequest
        {
            DisplayName = "Work", GitUserName = "Work", GitUserEmail = "w@example.com",
        });

        vault.Hold = true;
        var update = profiles.UpdateProfileAsync(p.Id, new GitProfileUpdateRequest { GitHubToken = "secret-token" });
        await vault.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var delete = profiles.DeleteProfileAsync(p.Id);
        await Task.Delay(50); // long enough for an unserialized delete to run to completion
        vault.Release.SetResult();

        try { await update; }
        catch (ProfileValidationException ex) { Assert.Equal("Profile not found.", ex.Message); }
        var result = await delete;

        Assert.Equal(ProfileDeleteResult.Deleted, result);
        Assert.Null(profiles.GetProfileById(p.Id));
        Assert.Equal(0, vault.Count);
        await profiles.DisposeAsync();
    }

    [Fact(Timeout = 30_000)]
    public async Task Concurrent_creates_with_the_same_name_admit_one()
    {
        var vault = new GatedVault();
        var profiles = new ProfileService(_settings, vault, new NoGitCli());
        GitProfileCreateRequest Request() => new()
        {
            DisplayName = "Same", GitUserName = "Same", GitUserEmail = "s@example.com", GitHubToken = "token",
        };

        vault.Hold = true;
        var first = profiles.CreateProfileAsync(Request());
        await vault.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vault.Hold = false;
        var second = profiles.CreateProfileAsync(Request());
        await Task.Delay(50);
        vault.Release.SetResult();

        var outcomes = await Task.WhenAll(Settle(first), Settle(second));

        Assert.Equal(1, outcomes.Count(ok => ok));
        Assert.Equal(2, profiles.GetAllProfiles().Count); // the global profile and one "Same"
        await profiles.DisposeAsync();

        static async Task<bool> Settle(Task<GitProfile> create)
        {
            try { await create; return true; }
            catch (ProfileValidationException) { return false; }
        }
    }

    [Fact]
    public async Task Racing_binds_leave_the_newest_bindings_pending()
    {
        const int rounds = 1_000;
        var key = RepoIdentityKey.FromRootCommit("abc123abc123abc123abc123abc123abc123abcd");
        var writer = typeof(ProfileService).GetField("_bindingsWriter", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_profiles)!;
        var pendingOf = typeof(AtomicFileWriter).GetProperty("PendingContentForTesting", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        var inversions = 0;
        using var barrier = new Barrier(2);
        for (var r = 0; r < rounds; r++)
        {
            async Task Bind(Guid id)
            {
                barrier.SignalAndWait();
                await _profiles.BindProfileToRepositoryAsync(key, "C:/repos/x", id);
            }
            await Task.WhenAll(Task.Run(() => Bind(idA)), Task.Run(() => Bind(idB)));

            // A debounce that fired between rounds already put a snapshot on disk; nothing to compare.
            if (pendingOf.GetValue(writer) is not string pending)
                continue;
            using var doc = JsonDocument.Parse(pending);
            var queued = doc.RootElement.GetProperty("bindings").GetProperty(key.Value).GetProperty("profileId").GetGuid();
            if (queued != _profiles.GetAllRepoBindings()[key].ProfileId)
                inversions++;
        }

        Assert.Equal(0, inversions);
    }

    [Fact]
    public async Task Import_still_creates_every_profile()
    {
        await Create("Personal");
        await Create("Work", "w@example.com");
        var file = Path.Combine(_state.Path, "profiles-export.json");
        await _profiles.ExportProfilesAsync(file, includeTokens: false, password: null);

        // The export carries the synthesized global profile too, which has no identity here and
        // would fail import validation; this test is about the profiles a user created.
        var bundle = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        var entries = bundle["profiles"]!.AsArray();
        for (var i = entries.Count - 1; i >= 0; i--)
            if (entries[i]!["profile"]!["isGlobal"]?.GetValue<bool>() == true)
                entries.RemoveAt(i);
        Assert.Equal(2, entries.Count);
        await File.WriteAllTextAsync(file, bundle.ToJsonString());
        var before = _profiles.GetAllProfiles().Count;

        await _profiles.ImportProfilesAsync(file, password: null);

        var after = _profiles.GetAllProfiles();
        Assert.Equal(before + 2, after.Count);
        Assert.Equal(after.Count, after.Select(p => p.DisplayName.ToLowerInvariant()).Distinct().Count());
    }

    public void Dispose()
    {
        _profiles.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _settings.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _state.Dispose();
    }
}
