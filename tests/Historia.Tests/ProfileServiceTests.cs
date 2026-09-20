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

    public void Dispose()
    {
        _profiles.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _settings.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _state.Dispose();
    }
}
