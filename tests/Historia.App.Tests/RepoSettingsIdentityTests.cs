namespace HistoriaApp.Tests;
[Trait("tier", "ui")]

public class RepoSettingsIdentityTests : IDisposable
{
    private readonly string _repoDir;
    private readonly ServiceProvider _sp;
    private readonly RepositoryContext _context;
    private readonly FakeLayoutService _layoutService = new();
    private readonly FakeProfileService _profileService = new();
    private readonly FakeWorkspaceService _workspaceService = new();

    public RepoSettingsIdentityTests()
    {
        _repoDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "historia-repo-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoDir);
        LibGit2Sharp.Repository.Init(_repoDir);

        var sc = new ServiceCollection();
        var settings = SettingsService.Load();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<IRepoLayoutService>(_layoutService);
        sc.AddSingleton<IProfileService>(_profileService);
        sc.AddSingleton<IWorkspaceService>(_workspaceService);
        var locator = new GitExecutableLocator();
        locator.ResolveAsync(null).GetAwaiter().GetResult();
        sc.AddSingleton<IGitExecutableLocator>(locator);
        sc.AddSingleton<IGitCliService, GitCliService>();
        sc.AddSingleton<ICredentialVaultService, FakeVault>();
        _sp = sc.BuildServiceProvider();

        _context = RepositoryContext.Open(_repoDir, _sp);
    }

    public void Dispose()
    {
        try { _context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _sp.Dispose(); } catch { }
        try { Directory.Delete(_repoDir, true); } catch { }
    }

    [AvaloniaFact]
    public void Shows_identity_and_ssh_only_for_a_focused_repo()
    {
        _workspaceService.ChangeFocus(_context);

        var view = new RepoSettingsView(_sp);
        view.Measure(new Size(800, 600));
        view.Arrange(new Rect(0, 0, 800, 600));

        var toggle = view.GetLogicalDescendants().OfType<ToggleSwitch>().FirstOrDefault();
        Assert.NotNull(toggle);
        Assert.Equal("SSH only", toggle.Content);

        var identityBtn = view.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Content is string s && s.Contains("▾"));
        Assert.NotNull(identityBtn);
        Assert.Equal("Local config  ▾", identityBtn.Content);

        view.Dispose();
    }

    [AvaloniaFact]
    public void Hides_them_with_no_repo()
    {
        _workspaceService.ChangeFocus(null);

        var view = new RepoSettingsView(_sp);
        view.Measure(new Size(800, 600));
        view.Arrange(new Rect(0, 0, 800, 600));

        var toggle = view.GetLogicalDescendants().OfType<ToggleSwitch>().FirstOrDefault();
        Assert.Null(toggle);

        var identityBtn = view.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Content is string s && s.Contains("▾"));
        Assert.Null(identityBtn);

        view.Dispose();
    }

    [AvaloniaFact]
    public async Task Toggling_ssh_only_writes_repo_layout()
    {
        _workspaceService.ChangeFocus(_context);

        var view = new RepoSettingsView(_sp);
        view.Measure(new Size(800, 600));
        view.Arrange(new Rect(0, 0, 800, 600));

        var toggle = view.GetLogicalDescendants().OfType<ToggleSwitch>().First();
        Assert.False(toggle.IsChecked);

        toggle.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        // Give the async update a moment to finish
        for (var i = 0; i < 20 && !_layoutService.Get(_context.RepositoryPath).SshOnly; i++)
            await Task.Delay(20);

        Assert.True(_layoutService.Get(_context.RepositoryPath).SshOnly);

        view.Dispose();
    }

    [AvaloniaFact]
    public void Stale_profile_renders_unavailable()
    {
        _workspaceService.ChangeFocus(_context);

        var deletedId = Guid.NewGuid();
        var staleProfile = new GitProfile
        {
            Id = deletedId,
            DisplayName = "OldDev",
            GitUserName = "olduser",
            GitUserEmail = "old@test.com",
            IsGlobal = false,
        };
        _context.State.SetActiveProfile(staleProfile);

        var view = new RepoSettingsView(_sp);
        view.Measure(new Size(800, 600));
        view.Arrange(new Rect(0, 0, 800, 600));

        var identityBtn = view.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Content is string s && s.Contains("▾"));
        Assert.NotNull(identityBtn);
        Assert.Contains("OldDev (unavailable)", (string)identityBtn.Content!);

        view.Dispose();
    }

    private const string IdentityHint = "Name, email and signing key used for commits in this repository.";
    private const string SshOnlyHint = "Fetch, pull and push always use SSH, even for HTTPS remotes. HTTPS is never tried.";
    private const string GraphColumnsHint = "Order and visibility apply to all repositories. Widths are per repository and reset on restart.";

    [AvaloniaFact]
    public void Repo_settings_hints_have_their_full_text()
    {
        _workspaceService.ChangeFocus(_context);

        var view = new RepoSettingsView(_sp);
        view.Measure(new Size(800, 600));
        view.Arrange(new Rect(0, 0, 800, 600));

        var expected = new Dictionary<string, string>
        {
            ["Identity"] = IdentityHint,
            ["SSH only"] = SshOnlyHint,
            ["Graph columns"] = GraphColumnsHint,
        };
        Assert.Equal(expected.OrderBy(p => p.Key, StringComparer.Ordinal),
                     HelpHintMap.Of(view).OrderBy(p => p.Key, StringComparer.Ordinal));

        var tips = HelpHintMap.Badges(view).Select(ToolTip.GetTip).ToHashSet();
        Assert.DoesNotContain(view.GetLogicalDescendants().OfType<TextBlock>(),
            t => !tips.Contains(t) && t.TextWrapping == TextWrapping.Wrap);

        view.Dispose();
    }

    [AvaloniaFact]
    public void Graph_columns_hint_shows_without_a_focused_repo()
    {
        _workspaceService.ChangeFocus(null);

        var view = new RepoSettingsView(_sp);
        view.Measure(new Size(800, 600));
        view.Arrange(new Rect(0, 0, 800, 600));

        var map = HelpHintMap.Of(view);
        Assert.Equal(new Dictionary<string, string> { ["Graph columns"] = GraphColumnsHint }, map);

        view.Dispose();
    }

    private sealed class FakeLayoutService : IRepoLayoutService
    {
        private readonly Dictionary<string, RepoLayout> _layouts = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<GraphColumn> GlobalColumns => GraphColumnDefaults.All;

        public event EventHandler<RepoLayoutChangedEventArgs>? Changed;

        public RepoLayout Get(string repoPath)
        {
            var key = IRepoLayoutService.KeyFor(repoPath);
            return _layouts.TryGetValue(key, out var l) ? l : new RepoLayout();
        }

        public Task UpdateAsync(string repoPath, Func<RepoLayout, RepoLayout> mutate)
        {
            var key = IRepoLayoutService.KeyFor(repoPath);
            var current = Get(repoPath);
            var next = mutate(current);
            _layouts[key] = next;
            Changed?.Invoke(this, new RepoLayoutChangedEventArgs(repoPath, next));
            return Task.CompletedTask;
        }

        public Task UpdateGlobalAsync(Func<IReadOnlyList<GraphColumn>, IReadOnlyList<GraphColumn>> mutate)
            => Task.CompletedTask;

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeProfileService : IProfileService
    {
        private readonly List<GitProfile> _profiles = [];

#pragma warning disable CS0067
        public event EventHandler? ProfilesChanged;
#pragma warning restore CS0067

        public IReadOnlyList<GitProfile> GetAllProfiles() => _profiles;
        public GitProfile? GetProfileById(Guid id) => _profiles.FirstOrDefault(p => p.Id == id);
        public ProfileUsageState? GetUsage(Guid id) => null;
        public Task<GitProfile> CreateProfileAsync(GitProfileCreateRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitProfile> UpdateProfileAsync(Guid id, GitProfileUpdateRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ProfileDeleteResult> DeleteProfileAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public GitProfile GetGlobalProfile() => new()
        {
            Id = Guid.NewGuid(),
            DisplayName = "Global",
            GitUserName = "global",
            GitUserEmail = "global@test.com",
            IsGlobal = true,
        };
        public Task SyncGlobalIdentityAsync(CancellationToken ct = default) => Task.CompletedTask;
        public GitProfile? GetDefaultProfile() => null;
        public Task SetDefaultProfileAsync(Guid? profileId, CancellationToken ct = default) => Task.CompletedTask;
        public GitProfile? GetProfileForRepository(RepoIdentityKey repoKey) => null;
        public bool IsLocalConfigSelected(RepoIdentityKey repoKey) => false;
        public Task BindProfileToRepositoryAsync(RepoIdentityKey repoKey, string displayPath, Guid profileId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLocalConfigForRepositoryAsync(RepoIdentityKey repoKey, string displayPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnbindRepositoryAsync(RepoIdentityKey repoKey, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyDictionary<RepoIdentityKey, RepoProfileBinding> GetAllRepoBindings() => new Dictionary<RepoIdentityKey, RepoProfileBinding>();
        public IReadOnlyList<RepoProfileBinding> GetStaleBindings() => [];
        public Task<int> CleanStaleBindingsAsync(CancellationToken ct = default) => Task.FromResult(0);
        public GitProfile? ResolveActiveProfile(RepoIdentityKey? repoKey = null) => null;
        public Task ApplyProfileToGitConfigAsync(GitProfile profile, string repoPath, GitConfigScope scope, CancellationToken ct = default) => Task.CompletedTask;
        public Task TouchProfileAsync(Guid profileId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetTokenAsync(Guid profileId, RemoteHost host, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task ExportProfilesAsync(string exportFilePath, bool includeTokens, string? password, CancellationToken ct = default) => Task.CompletedTask;
        public Task ImportProfilesAsync(string importFilePath, string? password, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeWorkspaceService : IWorkspaceService
    {
        public RepositoryContext? Focused { get; private set; }
        public IReadOnlyList<RepositoryContext> OpenRepositories => Focused is not null ? [Focused] : [];

        public event EventHandler<FocusedRepositoryChangedEventArgs>? FocusedRepositoryChanged;
#pragma warning disable CS0067
        public event EventHandler<OpenRepositoriesChangedEventArgs>? OpenRepositoriesChanged;
#pragma warning restore CS0067

        public void ChangeFocus(RepositoryContext? ctx)
        {
            var old = Focused;
            Focused = ctx;
            FocusedRepositoryChanged?.Invoke(this, new FocusedRepositoryChangedEventArgs(old, ctx));
        }

        public void Focus(RepositoryContext context) => ChangeFocus(context);
        public Task<RepositoryContext> OpenAsync(string repositoryPath, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RepositoryContext> InitAsync(string path, string? initialBranch, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RepositoryContext> CloneAsync(string url, string targetDirectory, CloneOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RepositoryContext> OpenSubmoduleAsync(RepositoryContext owner, SubmoduleInfo submodule, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CloseAsync(RepositoryContext context) => Task.CompletedTask;
        public void EnsureInitialized(RepositoryContext context) { }
        public void Move(RepositoryContext context, int newIndex) { }
        public Task RestoreSessionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeVault : ICredentialVaultService
    {
        public Task StoreTokenAsync(string key, string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> RetrieveTokenAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteTokenAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }
}
