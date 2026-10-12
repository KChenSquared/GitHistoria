namespace Historia.Tests;

/// <summary>
/// The Add remote, Edit remote and About dialogs' view models: live validation of the name and
/// URL, what each submit command produces, and the version line and credits.
/// </summary>
[Trait("tier", "unit")]
public class RemoteDialogViewModelTests
{
    private static RemoteInfo Remote(string url, string? pushUrl = null, string name = "origin") =>
        new() { Name = name, Url = url, PushUrl = pushUrl ?? url };

    [Theory]
    [InlineData("", "https://a/r.git", false)]
    [InlineData("has space", "https://a/r.git", false)]
    [InlineData("/lead", "https://a/r.git", false)]
    [InlineData("trail/", "https://a/r.git", false)]
    [InlineData("a..b", "https://a/r.git", false)]
    [InlineData("taken", "https://a/r.git", false)]
    [InlineData("origin", "", false)]
    [InlineData("origin", "https://a/r .git", false)]
    [InlineData("origin", "https://a/r.git", true)]
    [InlineData("team/fork", "https://a/r.git", true)]
    [InlineData("-x", "https://a/r.git", false)]
    [InlineData("--upload-pack=evil", "https://a/r.git", false)]
    [InlineData("origin", "git@host:o/taken.git", false)]
    [InlineData("origin", "  git@host:o/taken.git  ", false)]
    public void Validate_accepts_only_usable_names_and_urls(string name, string url, bool valid)
    {
        var problem = RemoteDialogViewModel.Validate(name, url, ["taken", "other"],
            new Dictionary<string, string> { ["git@host:o/taken.git"] = "taken" });

        Assert.Equal(valid, problem is null);
    }

    [Fact]
    public void Validate_duplicate_url_names_the_remote_using_it()
    {
        var owners = RemoteDialogViewModel.UrlOwners(
            [Remote("git@host:o/up.git", name: "upstream"), Remote(" git@host:o/r.git ", name: "origin")]);

        Assert.Equal("Remote 'upstream' already uses this URL.",
            RemoteDialogViewModel.Validate("fork", "git@host:o/up.git", ["upstream", "origin"], owners));
        Assert.Equal("Remote 'origin' already uses this URL.",
            RemoteDialogViewModel.Validate("fork", "git@host:o/r.git ", ["upstream", "origin"], owners));
    }

    [Fact]
    public void Validate_rejects_a_leading_dash_with_its_own_message() =>
        Assert.Equal("A remote name cannot start with '-'.",
            RemoteDialogViewModel.Validate("-x", "git@host:o/r.git", [], new Dictionary<string, string>()));

    [Fact]
    public void UrlOwners_keeps_the_first_remote_for_a_shared_url()
    {
        var owners = RemoteDialogViewModel.UrlOwners(
            [Remote("git@host:o/r.git", name: "first"), Remote(" git@host:o/r.git", name: "second")]);

        Assert.Equal("first", Assert.Single(owners).Value);
    }

    [Fact]
    public void Add_without_remotes_starts_as_origin_with_a_quiet_form()
    {
        var vm = new AddRemoteDialogViewModel([]);

        Assert.Equal("origin", vm.Name);
        Assert.True(vm.Fetch);
        Assert.True(vm.FocusUrlFirst);
        Assert.False(vm.IsValid);
        Assert.False(vm.ShowProblem);
        Assert.False(vm.AddCommand.CanExecute(null));
    }

    [Fact]
    public void Add_with_remotes_starts_empty_and_focuses_the_name()
    {
        var vm = new AddRemoteDialogViewModel([Remote("git@host:o/r.git")]);

        Assert.Equal("", vm.Name);
        Assert.False(vm.FocusUrlFirst);
        Assert.False(vm.ShowProblem);
        Assert.False(vm.AddCommand.CanExecute(null));
    }

    [Fact]
    public void Add_shows_a_problem_once_the_user_has_typed_and_enables_add_when_valid()
    {
        var vm = new AddRemoteDialogViewModel([Remote("git@host:o/r.git")]);

        vm.Name = "origin";
        Assert.True(vm.ShowProblem);
        Assert.Equal("A remote named 'origin' already exists.", vm.Problem);

        vm.Name = "upstream";
        vm.Url = "git@host:o/r.git";
        Assert.Equal("Remote 'origin' already uses this URL.", vm.Problem);
        Assert.False(vm.AddCommand.CanExecute(null));

        vm.Url = "git@host:o/up.git";
        Assert.Null(vm.Problem);
        Assert.False(vm.ShowProblem);
        Assert.True(vm.AddCommand.CanExecute(null));
    }

    [Fact]
    public void Add_raises_can_execute_changed_when_the_input_becomes_valid()
    {
        var vm = new AddRemoteDialogViewModel([]);
        var changes = 0;
        vm.AddCommand.CanExecuteChanged += (_, _) => changes++;

        vm.Url = "git@host:o/r.git";

        Assert.True(changes > 0);
    }

    [Fact]
    public void Add_returns_the_trimmed_values_and_the_fetch_choice_then_closes()
    {
        var vm = new AddRemoteDialogViewModel([]);
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;
        vm.Name = "  upstream ";
        vm.Url = " git@host:o/up.git  ";
        vm.Fetch = false;

        vm.AddCommand.Execute(null);

        Assert.Equal(new AddRemoteResult("upstream", "git@host:o/up.git", false), vm.Result);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void Add_cancel_returns_nothing_and_closes()
    {
        var vm = new AddRemoteDialogViewModel([]);
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;
        vm.Url = "git@host:o/up.git";

        vm.CancelCommand.Execute(null);

        Assert.Null(vm.Result);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void Add_cannot_submit_invalid_input()
    {
        var vm = new AddRemoteDialogViewModel([]);
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;
        vm.Url = "has space";

        vm.AddCommand.Execute(null);

        Assert.Null(vm.Result);
        Assert.Equal(0, closed);
    }

    [Fact]
    public void Edit_starts_from_the_remote_and_shows_no_problem()
    {
        var vm = new EditRemoteDialogViewModel(Remote("git@host:o/r.git"), [], new Dictionary<string, string>());

        Assert.Equal("origin", vm.Name);
        Assert.Equal("git@host:o/r.git", vm.Url);
        Assert.Null(vm.Problem);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.HasDistinctPushUrl);
    }

    [Fact]
    public void Edit_accepts_a_remote_keeping_its_own_url()
    {
        var origin = Remote("git@host:o/r.git");
        var others = new[] { Remote("git@host:o/up.git", name: "upstream") };

        var vm = new EditRemoteDialogViewModel(origin, others.Select(r => r.Name).ToList(), RemoteDialogViewModel.UrlOwners(others));

        Assert.Null(vm.Problem);
    }

    [Fact]
    public void Edit_shows_a_problem_immediately_and_disables_save()
    {
        var vm = new EditRemoteDialogViewModel(Remote("git@host:o/r.git"), ["upstream"], new Dictionary<string, string>());

        vm.Name = "upstream";

        Assert.True(vm.ShowProblem);
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void Edit_cannot_save_invalid_input()
    {
        var vm = new EditRemoteDialogViewModel(Remote("git@host:o/r.git"), [], new Dictionary<string, string>());
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;
        vm.Name = "has space";

        vm.SaveCommand.Execute(null);

        Assert.Null(vm.Result);
        Assert.Equal(0, closed);
    }

    [Fact]
    public void Edit_saved_unchanged_returns_nothing_and_closes()
    {
        var vm = new EditRemoteDialogViewModel(Remote("git@host:o/r.git"), [], new Dictionary<string, string>());
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        vm.SaveCommand.Execute(null);

        Assert.Null(vm.Result);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void Edit_returns_the_trimmed_changes()
    {
        var vm = new EditRemoteDialogViewModel(Remote("git@host:o/r.git"), [], new Dictionary<string, string>());
        vm.Name = " renamed ";
        vm.Url = " git@host:o/new.git ";

        vm.SaveCommand.Execute(null);

        Assert.Equal(new EditRemoteResult("renamed", "git@host:o/new.git", false), vm.Result);
    }

    [Fact]
    public void Edit_reset_push_url_counts_as_a_change_when_a_separate_push_url_exists()
    {
        var vm = new EditRemoteDialogViewModel(
            Remote("git@host:o/r.git", pushUrl: "git@host:o/push.git"), [], new Dictionary<string, string>());

        Assert.True(vm.HasDistinctPushUrl);
        Assert.Equal("Reset push URL (currently git@host:o/push.git)", vm.ResetPushUrlLabel);

        vm.ResetPushUrl = true;
        vm.SaveCommand.Execute(null);

        Assert.Equal(new EditRemoteResult("origin", "git@host:o/r.git", true), vm.Result);
    }

    [Fact]
    public void Edit_ignores_reset_push_url_without_a_separate_push_url()
    {
        var vm = new EditRemoteDialogViewModel(Remote("git@host:o/r.git"), [], new Dictionary<string, string>());

        vm.ResetPushUrl = true;
        vm.SaveCommand.Execute(null);

        Assert.Null(vm.Result);
    }

    [Fact]
    public void Edit_cancel_returns_nothing_and_closes()
    {
        var vm = new EditRemoteDialogViewModel(Remote("git@host:o/r.git"), [], new Dictionary<string, string>());
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;
        vm.Name = "renamed";

        vm.CancelCommand.Execute(null);

        Assert.Null(vm.Result);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void About_formats_the_version_with_a_short_sha() =>
        Assert.Equal("Version 1.0.0 (dac352a)",
            new AboutDialogViewModel("1.0.0+dac352a1e8b7c9d0e1f2a3b4c5d6e7f8a9b0c1d2").VersionText);

    [Theory]
    [InlineData("1.0.0", "Version 1.0.0")]
    [InlineData("1.0.0+", "Version 1.0.0")]
    [InlineData("1.2.3+abc", "Version 1.2.3 (abc)")]
    [InlineData(null, "Version unknown")]
    [InlineData("", "Version unknown")]
    [InlineData("   ", "Version unknown")]
    [InlineData("+dac352a", "Version unknown")]
    public void About_FormatVersion_cases(string? input, string expected) =>
        Assert.Equal(expected, AboutDialogViewModel.FormatVersion(input));

    [Fact]
    public void About_credits_keep_the_three_attributions()
    {
        var credits = new AboutDialogViewModel(null).Credits;

        Assert.Equal(3, credits.Count);
        Assert.Contains(credits, c => c.Contains("Material Symbols") && c.Contains("Apache License 2.0"));
        Assert.Contains(credits, c => c.Contains("SourceGit") && c.Contains("MIT License"));
        Assert.Contains(credits, c => c.Contains("AvaloniaEdit") && c.Contains("TextMateSharp") && c.Contains("DiffPlex"));
    }
}
