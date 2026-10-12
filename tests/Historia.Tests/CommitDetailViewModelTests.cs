namespace Historia.Tests;

/// <summary>
/// The commit detail view model: a debounced load that a newer commit supersedes, the header
/// fields, the files list in path and tree mode with its folder collapsing, and the commands that
/// reach the state service, the launcher and the clipboard.
/// </summary>
[Trait("tier", "unit")]
public class CommitDetailViewModelTests
{
    private static readonly GitObjectId IdA = GitObjectId.Parse(new string('a', 40));
    private static readonly GitObjectId IdB = GitObjectId.Parse(new string('b', 40));
    private static readonly GitObjectId IdC = GitObjectId.Parse(new string('c', 40));

    public class GatewayProxy : DispatchProxy
    {
        public readonly object Sync = new();
        public List<(GitObjectId Id, CancellationToken Ct)> DetailCalls { get; } = [];
        public Func<GitObjectId, CancellationToken, Task<CommitDetail>> DetailHandler { get; set; } = (_, _) => throw new InvalidOperationException("no detail handler");
        public Func<GitObjectId, CancellationToken, Task<IReadOnlyList<FileStatusEntry>>> FilesHandler { get; set; } = (_, _) => Task.FromResult<IReadOnlyList<FileStatusEntry>>([]);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_RepositoryPath":
                    return "/repo";
                case nameof(IGitGateway.GetCommitDetailAsync):
                    var id = (GitObjectId)args![0]!;
                    var ct = (CancellationToken)args[1]!;
                    lock (Sync)
                        DetailCalls.Add((id, ct));
                    return DetailHandler(id, ct);
                case nameof(IGitGateway.GetCommitFilesAsync):
                    return FilesHandler((GitObjectId)args![0]!, (CancellationToken)args[2]!);
                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }

    public class StateProxy : DispatchProxy
    {
        public List<GitObjectId?> Selected { get; } = [];
        public List<GitObjectId> Revealed { get; } = [];
        public List<DiffRequest?> OpenedDiffs { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IRepositoryStateService.SetSelectedCommit):
                    Selected.Add((GitObjectId?)args![0]);
                    return null;
                case nameof(IRepositoryStateService.RequestRevealCommit):
                    Revealed.Add((GitObjectId)args![0]!);
                    return null;
                case nameof(IRepositoryStateService.SetOpenDiff):
                    OpenedDiffs.Add((DiffRequest?)args![0]);
                    return null;
                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }

    private sealed class RecordingLauncher : IExternalLauncher
    {
        public List<string> OpenedUrls { get; } = [];
        public List<string> Revealed { get; } = [];

        public void OpenUrl(string url) => OpenedUrls.Add(url);

        public void RevealInFileManager(string path) => Revealed.Add(path);

        public void OpenFolder(string path)
        {
        }
    }

    private sealed class Rig : IDisposable
    {
        public Rig(
            AppSettings? settings = null,
            IUiDispatcher? ui = null,
            GitObjectId? id = null,
            string[]? files = null,
            Func<GitObjectId, CancellationToken, Task<CommitDetail>>? detail = null,
            Func<GitObjectId, CancellationToken, Task<IReadOnlyList<FileStatusEntry>>>? filesHandler = null)
        {
            Settings = new FakeSettingsService(settings ?? new AppSettings());
            Notifications = new FakeNotificationService();
            Clipboard = new FakeClipboardService();
            Launcher = new RecordingLauncher();
            Gateway = (GatewayProxy)(object)DispatchProxy.Create<IGitGateway, GatewayProxy>();
            State = (StateProxy)(object)DispatchProxy.Create<IRepositoryStateService, StateProxy>();
            Gateway.DetailHandler = detail ?? ((i, _) => Task.FromResult(MakeDetail(i, parents: [IdB])));
            Gateway.FilesHandler = filesHandler ?? ((_, _) => Task.FromResult(Files(files ?? ["src/a.cs"])));

            ViewModel = new CommitDetailViewModel(
                (IGitGateway)(object)Gateway,
                (IRepositoryStateService)(object)State,
                "repo-1",
                id ?? IdA,
                Settings,
                Notifications,
                Launcher,
                ui ?? new ImmediateUiDispatcher(),
                Clipboard);
        }

        public FakeSettingsService Settings { get; }
        public FakeNotificationService Notifications { get; }
        public FakeClipboardService Clipboard { get; }
        public RecordingLauncher Launcher { get; }
        public GatewayProxy Gateway { get; }
        public StateProxy State { get; }
        public CommitDetailViewModel ViewModel { get; }

        public async Task LoadedAsync() => await WaitUntilAsync(() => ViewModel.HasDetail);

        public void Dispose() => ViewModel.Dispose();
    }

    private static CommitDetail MakeDetail(GitObjectId id, string message = "subject\n\nbody", params GitObjectId[] parents) => new()
    {
        Id = id,
        Message = message,
        MessageShort = "subject",
        MessageBody = "body",
        AuthorName = "Ann Author",
        AuthorEmail = "ann@example.com",
        AuthorWhen = new DateTimeOffset(2024, 3, 5, 10, 30, 0, TimeSpan.Zero),
        CommitterName = "Cy Committer",
        CommitterEmail = "cy@example.com",
        CommitWhen = new DateTimeOffset(2024, 3, 7, 12, 45, 0, TimeSpan.Zero),
        ParentIds = parents,
    };

    private static IReadOnlyList<FileStatusEntry> Files(params string[] paths) =>
        [.. paths.Select(p => new FileStatusEntry { Path = p, Kind = FileChangeKind.Modified, IsBinary = false, IsSubmodule = false })];

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "The condition was not met in time.");
    }

    private static string Describe(CommitRowViewModel row) => row switch
    {
        CommitFolderRowViewModel f => $"{f.Chevron} {f.Label} @{f.Indent}",
        CommitFileRowViewModel f => $"{f.Display} @{f.Indent}",
        _ => "?",
    };

    private static readonly string[] TreeFiles =
    [
        "z.txt",
        "a/b/d.txt",
        "a/e.txt",
        "a/b/sub/f.txt",
        "a/b/c.txt",
    ];

    [Fact]
    public async Task A_new_view_model_is_loading_until_the_commit_arrives()
    {
        using var rig = new Rig();

        Assert.False(rig.ViewModel.HasDetail);
        Assert.Equal("Loading file changes…", rig.ViewModel.FilesStatus);
        Assert.True(rig.ViewModel.HasFilesStatus);
        Assert.Empty(rig.ViewModel.Rows);

        await rig.LoadedAsync();

        Assert.Equal("", rig.ViewModel.FilesStatus);
        Assert.False(rig.ViewModel.HasFilesStatus);
    }

    [Fact]
    public async Task The_header_shows_the_author_hash_date_and_parents()
    {
        using var rig = new Rig();
        await rig.LoadedAsync();
        var vm = rig.ViewModel;

        Assert.Equal("Ann Author", vm.AuthorName);
        Assert.Equal("ann@example.com", vm.AuthorEmail);
        Assert.Equal(IdA.ToShortString(10), vm.ShortHash);
        Assert.Equal(IdA.ToString(), vm.FullHash);
        Assert.Equal("2024-03-07", vm.DateText);
        Assert.Contains("Committed 2024-03-07 12:45", vm.DateToolTip);
        Assert.Contains("Authored 2024-03-05 10:30", vm.DateToolTip);
        Assert.Equal(["bbbbbbbbbb"], vm.Parents.Select(p => p.ShortHash));
        Assert.True(vm.HasParents);
        Assert.False(vm.HasNoParents);
    }

    [Fact]
    public async Task A_root_commit_has_no_parents()
    {
        using var rig = new Rig(detail: (i, _) => Task.FromResult(MakeDetail(i)));

        await rig.LoadedAsync();

        Assert.Empty(rig.ViewModel.Parents);
        Assert.False(rig.ViewModel.HasParents);
        Assert.True(rig.ViewModel.HasNoParents);
    }

    [Fact]
    public async Task The_message_is_reflowed_and_linked_by_the_autolink_rules()
    {
        var settings = new AppSettings
        {
            IssueAutolinks = [new IssueAutolinkRule { Pattern = @"#(\d+)", UrlTemplate = "https://tracker.invalid/$1" }],
        };
        using var rig = new Rig(settings, detail: (i, _) => Task.FromResult(MakeDetail(i, "fix: the bug\nfrom #42")));

        await rig.LoadedAsync();

        Assert.Equal(CommitMessageReflow.Reflow("fix: the bug\nfrom #42"), rig.ViewModel.MessageText);
        var link = Assert.Single(rig.ViewModel.MessageRuns, r => r.Url is not null);
        Assert.Equal("#42", link.Text);
        Assert.Equal("https://tracker.invalid/42", link.Url);

        rig.ViewModel.OpenUrlCommand.Execute(link.Url);
        Assert.Equal(["https://tracker.invalid/42"], rig.Launcher.OpenedUrls);
    }

    [Fact]
    public async Task Path_mode_lists_every_file_in_the_order_git_returned_it_with_no_indent()
    {
        using var rig = new Rig(files: TreeFiles);
        await rig.LoadedAsync();

        Assert.Equal(
            ["z.txt @12", "a/b/d.txt @12", "a/e.txt @12", "a/b/sub/f.txt @12", "a/b/c.txt @12"],
            rig.ViewModel.Rows.Select(Describe));
    }

    [Fact]
    public async Task Tree_mode_lists_folders_before_files_depth_first_in_path_order()
    {
        using var rig = new Rig(new AppSettings { CommitFilesView = CommitFilesView.Tree }, files: TreeFiles);
        await rig.LoadedAsync();

        Assert.Equal(
            [
                "▾ a/ @12",
                "▾ b/ @26",
                "▾ sub/ @40",
                "f.txt @68",
                "c.txt @54",
                "d.txt @54",
                "e.txt @40",
                "z.txt @12",
            ],
            rig.ViewModel.Rows.Select(Describe));
    }

    [Fact]
    public async Task A_file_row_in_tree_mode_carries_its_full_path_and_change_kind()
    {
        using var rig = new Rig(new AppSettings { CommitFilesView = CommitFilesView.Tree }, files: TreeFiles);
        await rig.LoadedAsync();

        var file = rig.ViewModel.Rows.OfType<CommitFileRowViewModel>().Single(r => r.Display == "f.txt");

        Assert.Equal("a/b/sub/f.txt", file.Path);
        Assert.Equal(FileChangeKind.Modified, file.Kind);
    }

    [Fact]
    public async Task Collapsing_a_folder_hides_what_is_inside_it_and_expanding_brings_it_back()
    {
        using var rig = new Rig(new AppSettings { CommitFilesView = CommitFilesView.Tree }, files: TreeFiles);
        await rig.LoadedAsync();
        var vm = rig.ViewModel;

        vm.ToggleFolder("a/b");

        Assert.Equal(["▾ a/ @12", "▸ b/ @26", "e.txt @40", "z.txt @12"], vm.Rows.Select(Describe));

        vm.ToggleFolder("a");

        Assert.Equal(["▸ a/ @12", "z.txt @12"], vm.Rows.Select(Describe));

        vm.ToggleFolder("a");
        vm.ToggleFolder("a/b");

        Assert.Equal(8, vm.Rows.Count);
    }

    [Fact]
    public async Task Clicking_a_folder_row_toggles_it_and_clicking_a_file_row_opens_its_diff()
    {
        using var rig = new Rig(new AppSettings { CommitFilesView = CommitFilesView.Tree }, files: TreeFiles);
        await rig.LoadedAsync();
        var vm = rig.ViewModel;

        vm.Rows.OfType<CommitFolderRowViewModel>().Single(r => r.Name == "sub").ActivateCommand.Execute(null);
        Assert.DoesNotContain(vm.Rows, r => r is CommitFileRowViewModel { Display: "f.txt" });

        vm.Rows.OfType<CommitFileRowViewModel>().Single(r => r.Display == "c.txt").ActivateCommand.Execute(null);

        var diff = Assert.Single(rig.State.OpenedDiffs)!;
        Assert.Equal(DiffSource.Commit, diff.Source);
        Assert.Equal("a/b/c.txt", diff.Path);
        Assert.Equal(IdA, diff.CommitId);
        Assert.Equal(1, diff.ParentIndex);
        Assert.Equal(rig.Settings.Current.DiffContextLines, diff.ContextLines);
    }

    [Fact]
    public async Task A_commit_without_files_says_so()
    {
        using var rig = new Rig(files: [], detail: (i, _) => Task.FromResult(MakeDetail(i)));

        await rig.LoadedAsync();

        Assert.Equal("No file changes.", rig.ViewModel.FilesStatus);
        Assert.Empty(rig.ViewModel.Rows);
    }

    [Fact]
    public async Task Choosing_a_mode_saves_the_setting_and_rebuilds_the_list_at_once()
    {
        using var rig = new Rig(files: TreeFiles);
        await rig.LoadedAsync();
        var vm = rig.ViewModel;
        Assert.True(vm.IsPathMode);

        await vm.ShowTreeCommand.ExecuteAsync(null);

        Assert.Equal(CommitFilesView.Tree, rig.Settings.Current.CommitFilesView);
        Assert.True(vm.IsTreeMode);
        Assert.False(vm.IsPathMode);
        Assert.IsType<CommitFolderRowViewModel>(vm.Rows[0]);

        await vm.ShowPathCommand.ExecuteAsync(null);

        Assert.Equal(CommitFilesView.Path, rig.Settings.Current.CommitFilesView);
        Assert.True(vm.IsPathMode);
        Assert.All(vm.Rows, r => Assert.IsType<CommitFileRowViewModel>(r));
    }

    [Fact]
    public async Task A_mode_change_from_elsewhere_reaches_the_list_through_the_ui_dispatcher()
    {
        var ui = new RecordingUiDispatcher();
        using var rig = new Rig(ui: ui, files: TreeFiles);
        await rig.LoadedAsync();

        await rig.Settings.UpdateAsync(s => s with { CommitFilesView = CommitFilesView.Tree });

        Assert.True(rig.ViewModel.IsPathMode);
        Assert.Equal(1, ui.PostCount);

        ui.DrainPosted();

        Assert.True(rig.ViewModel.IsTreeMode);
        Assert.IsType<CommitFolderRowViewModel>(rig.ViewModel.Rows[0]);
    }

    [Fact]
    public async Task The_shown_date_follows_the_author_date_setting_through_the_ui_dispatcher()
    {
        var ui = new RecordingUiDispatcher();
        using var rig = new Rig(ui: ui);
        await rig.LoadedAsync();
        var changed = new List<string?>();
        rig.ViewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Equal("2024-03-07", rig.ViewModel.DateText);

        await rig.Settings.UpdateAsync(s => s with { ShowAuthorDate = true });

        Assert.Equal(1, ui.PostCount);
        Assert.DoesNotContain(nameof(CommitDetailViewModel.DateText), changed);

        ui.DrainPosted();

        Assert.Equal("2024-03-05", rig.ViewModel.DateText);
        Assert.Contains(nameof(CommitDetailViewModel.DateText), changed);
    }

    [Fact]
    public async Task A_settings_change_after_dispose_changes_nothing()
    {
        var ui = new RecordingUiDispatcher();
        using var rig = new Rig(ui: ui);
        await rig.LoadedAsync();
        var changed = new List<string?>();
        rig.ViewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        rig.ViewModel.Dispose();

        await rig.Settings.UpdateAsync(s => s with { ShowAuthorDate = true, CommitFilesView = CommitFilesView.Tree });
        ui.DrainPosted();

        Assert.Equal(0, ui.PostCount);
        Assert.Empty(changed);
        Assert.True(rig.ViewModel.IsPathMode);
    }

    [Fact]
    public async Task A_post_queued_before_dispose_does_nothing_once_the_view_model_is_gone()
    {
        var ui = new RecordingUiDispatcher();
        using var rig = new Rig(ui: ui);
        await rig.LoadedAsync();

        var changed = new List<string?>();
        rig.ViewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        await rig.Settings.UpdateAsync(s => s with { ShowAuthorDate = true, CommitFilesView = CommitFilesView.Tree });
        rig.ViewModel.Dispose();
        ui.DrainPosted();

        Assert.Equal(2, ui.PostCount);
        Assert.Empty(changed);
        Assert.True(rig.ViewModel.IsPathMode);
    }

    [Fact]
    public async Task Setting_the_same_commit_again_does_not_reload()
    {
        using var rig = new Rig();
        await rig.LoadedAsync();

        rig.ViewModel.SetCommit(IdA);
        await Task.Delay(CommitDetailViewModel.LoadDebounce + TimeSpan.FromMilliseconds(80));

        Assert.Single(rig.Gateway.DetailCalls);
        Assert.True(rig.ViewModel.HasDetail);
    }

    [Fact]
    public async Task Setting_a_commit_twice_cancels_the_stale_load_and_shows_only_the_latest()
    {
        var gateA = new TaskCompletionSource<CommitDetail>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateB = new TaskCompletionSource<CommitDetail>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new Rig(
            detail: (i, _) => (i == IdA ? gateA : gateB).Task,
            filesHandler: (i, _) => Task.FromResult(Files(i == IdA ? "from-a.txt" : "from-b.txt")));
        var shownHashes = new List<string>();
        rig.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CommitDetailViewModel.FullHash))
                shownHashes.Add(rig.ViewModel.FullHash);
        };

        await WaitUntilAsync(() => rig.Gateway.DetailCalls.Count == 1);
        var staleToken = rig.Gateway.DetailCalls[0].Ct;

        rig.ViewModel.SetCommit(IdB);

        Assert.True(staleToken.IsCancellationRequested);
        Assert.Equal(IdB, rig.ViewModel.CommitId);
        await WaitUntilAsync(() => rig.Gateway.DetailCalls.Count == 2);

        // The cancelled read finishes after it was superseded, and nothing of it is shown.
        gateA.SetResult(MakeDetail(IdA));
        await Task.Delay(80);
        Assert.False(rig.ViewModel.HasDetail);
        Assert.Empty(rig.ViewModel.Rows);

        gateB.SetResult(MakeDetail(IdB));
        await rig.LoadedAsync();

        Assert.Equal(IdB.ToString(), rig.ViewModel.FullHash);
        Assert.Equal(["from-b.txt"], rig.ViewModel.Rows.Cast<CommitFileRowViewModel>().Select(r => r.Path));
        Assert.DoesNotContain(IdA.ToString(), shownHashes);
    }

    [Fact]
    public async Task A_load_superseded_during_the_debounce_never_reads_git()
    {
        using var rig = new Rig();

        rig.ViewModel.SetCommit(IdB);
        rig.ViewModel.SetCommit(IdC);
        await rig.LoadedAsync();

        var read = Assert.Single(rig.Gateway.DetailCalls);
        Assert.Equal(IdC, read.Id);
        Assert.Equal(IdC.ToString(), rig.ViewModel.FullHash);
    }

    [Fact]
    public async Task Showing_another_commit_drops_the_previous_one_while_the_new_one_loads()
    {
        var gate = new TaskCompletionSource<CommitDetail>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new Rig(detail: (i, _) => i == IdA ? Task.FromResult(MakeDetail(i)) : gate.Task);
        await rig.LoadedAsync();

        rig.ViewModel.SetCommit(IdB);

        Assert.False(rig.ViewModel.HasDetail);
        Assert.Equal("", rig.ViewModel.AuthorName);
        Assert.Equal("Loading file changes…", rig.ViewModel.FilesStatus);
        Assert.Empty(rig.ViewModel.Rows);

        gate.SetResult(MakeDetail(IdB));
        await rig.LoadedAsync();
        Assert.Equal(IdB.ToString(), rig.ViewModel.FullHash);
    }

    [Fact]
    public async Task A_failed_load_publishes_an_error_notice()
    {
        using var rig = new Rig(detail: (_, _) => Task.FromException<CommitDetail>(new InvalidOperationException("boom")));

        await WaitUntilAsync(() => rig.Notifications.Published.Count == 1);

        var notice = rig.Notifications.Published[0];
        Assert.Equal(NotificationSeverity.Error, notice.Severity);
        Assert.Equal("Could not load commit: boom", notice.Message);
        Assert.False(rig.ViewModel.HasDetail);
    }

    [Fact]
    public async Task Disposing_cancels_the_load_without_a_notice()
    {
        using var rig = new Rig(detail: (_, ct) =>
        {
            var pending = new TaskCompletionSource<CommitDetail>();
            ct.Register(() => pending.TrySetCanceled(ct));
            return pending.Task;
        });
        await WaitUntilAsync(() => rig.Gateway.DetailCalls.Count == 1);
        var token = rig.Gateway.DetailCalls[0].Ct;

        rig.ViewModel.Dispose();

        Assert.True(token.IsCancellationRequested);
        await Task.Delay(80);
        Assert.Empty(rig.Notifications.Published);
    }

    [Fact]
    public async Task Copying_the_hash_puts_the_full_hash_on_the_clipboard()
    {
        using var rig = new Rig();
        await rig.LoadedAsync();

        await rig.ViewModel.CopyHashCommand.ExecuteAsync(null);

        Assert.Equal(IdA.ToString(), rig.Clipboard.Last);
    }

    [Fact]
    public async Task Copying_the_hash_before_the_commit_arrives_does_nothing()
    {
        using var rig = new Rig(detail: (_, _) => new TaskCompletionSource<CommitDetail>().Task);

        await rig.ViewModel.CopyHashCommand.ExecuteAsync(null);

        Assert.Empty(rig.Clipboard.Copied);
    }

    [Fact]
    public async Task Going_to_a_parent_selects_and_reveals_it()
    {
        using var rig = new Rig();
        await rig.LoadedAsync();

        rig.ViewModel.GoToParentCommand.Execute(rig.ViewModel.Parents[0]);

        Assert.Equal([IdB], rig.State.Selected.Select(s => s!.Value));
        Assert.Equal([IdB], rig.State.Revealed);
    }

    [Fact]
    public async Task A_file_row_reveals_and_copies_its_absolute_path()
    {
        using var rig = new Rig(files: ["src/a.cs"]);
        await rig.LoadedAsync();
        var file = Assert.IsType<CommitFileRowViewModel>(rig.ViewModel.Rows[0]);
        var expected = System.IO.Path.Combine("/repo", "src/a.cs");

        file.RevealCommand.Execute(null);
        await file.CopyPathCommand.ExecuteAsync(null);

        Assert.Equal([expected], rig.Launcher.Revealed);
        Assert.Equal(expected, rig.Clipboard.Last);
    }

    [Fact]
    public async Task Five_thousand_files_flatten_in_both_modes()
    {
        var paths = Enumerable.Range(0, 5000).Select(i => $"dir{i % 50:D2}/file_{i:D4}.txt").ToArray();
        using var rig = new Rig(files: paths);
        await rig.LoadedAsync();

        Assert.Equal(5000, rig.ViewModel.Rows.Count);

        await rig.ViewModel.ShowTreeCommand.ExecuteAsync(null);

        Assert.Equal(5050, rig.ViewModel.Rows.Count);
        Assert.Equal(50, rig.ViewModel.Rows.OfType<CommitFolderRowViewModel>().Count());
    }
}
