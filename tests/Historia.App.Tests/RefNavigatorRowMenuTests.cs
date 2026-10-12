namespace HistoriaApp.Tests;

/// <summary>
/// The buttons and menus on ref navigator rows are bound to the navigator's commands with the row as
/// the parameter: a tag's delete button, a stash's, submodule's and worktree's menus, a branch row's
/// context menu and its double-click, and the filter box taking focus when it opens.
/// </summary>
[Trait("tier", "ui")]
public sealed class RefNavigatorRowMenuTests
{
    private sealed class ScriptedDialogs : IDialogService
    {
        public bool ConfirmAnswer { get; set; }
        public List<string> DestructiveEffects { get; } = [];

        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel)
        {
            DestructiveEffects.Add(effect);
            return Task.FromResult(ConfirmAnswer);
        }

        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, bool destructive = false,
            string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None) => Task.FromResult(false);

        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options,
            int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None) => Task.FromResult(-1);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

        public Task<string?> PromptAsync(string title, string message, string? defaultValue = null,
            string confirmLabel = "OK", bool primaryConfirm = false) => Task.FromResult<string?>(null);
    }

    private sealed class RecordingStashService : IStashService
    {
        public IReadOnlyList<StashEntry> Entries { get; init; } = [];
        public List<GitObjectId> Applied { get; } = [];

        public Task<IReadOnlyList<StashEntry>> ListAsync(RepositoryContext context, CancellationToken ct = default) => Task.FromResult(Entries);

        public Task ApplyAsync(RepositoryContext context, GitObjectId commitId, bool restoreIndex, CancellationToken ct = default)
        {
            Applied.Add(commitId);
            return Task.CompletedTask;
        }

        public Task<bool> SaveAsync(RepositoryContext context, StashSaveOptions options, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> StashStagedAsync(RepositoryContext context, string? message, CancellationToken ct = default) => Task.FromResult(true);
        public Task ApplyAndDropAsync(RepositoryContext context, GitObjectId commitId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DropAsync(RepositoryContext context, GitObjectId commitId, CancellationToken ct = default) => Task.CompletedTask;
        public Task BranchFromAsync(RepositoryContext context, GitObjectId commitId, string branchName, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(RepositoryContext context, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FixedSubmoduleService(IReadOnlyList<SubmoduleInfo> entries) : ISubmoduleService
    {
        public Task<IReadOnlyList<SubmoduleInfo>> ListAsync(RepositoryContext context, CancellationToken ct = default) => Task.FromResult(entries);
        public Task AddAsync(RepositoryContext context, string url, string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task InitAndUpdateAsync(RepositoryContext context, string? path, bool recursive, CancellationToken ct = default) => Task.CompletedTask;
        public Task SyncAsync(RepositoryContext context, string? path, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveAsync(RepositoryContext context, SubmoduleInfo submodule, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FixedWorktreeService(IReadOnlyList<WorktreeInfo> entries) : IWorktreeService
    {
        public Task<IReadOnlyList<WorktreeInfo>> ListAsync(RepositoryContext context, CancellationToken ct = default) => Task.FromResult(entries);
        public Task AddAsync(RepositoryContext context, WorktreeAddRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveAsync(RepositoryContext context, string path, bool force, CancellationToken ct = default) => Task.CompletedTask;
        public Task LockAsync(RepositoryContext context, string path, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlockAsync(RepositoryContext context, string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveAsync(RepositoryContext context, string path, string newPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task PruneAsync(RepositoryContext context, CancellationToken ct = default) => Task.CompletedTask;
        public Task RepairAsync(RepositoryContext context, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static void Wait(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            System.Threading.Thread.Sleep(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static Border RowWith(ListBox list, Func<RefRowViewModel, bool> match) =>
        RefNavigatorTestHost.Rows(list).First(r => r.DataContext is RefRowViewModel row && match(row));

    /// <summary>A menu item as it reads while its menu is open: its bindings only resolve then.</summary>
    private sealed record Item(string? Header, System.Windows.Input.ICommand? Command, object? Parameter, bool IsVisible);

    private static List<Item> Snapshot(IEnumerable<MenuItem> items) =>
        [.. items.Select(i => new Item(i.Header?.ToString(), i.Command, i.CommandParameter, i.IsVisible))];

    /// <summary>Opens the button's flyout, reads its menu items, then closes it.</summary>
    private static List<Item> FlyoutItems(Button button)
    {
        var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
        flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        var items = Snapshot(flyout.Items.OfType<MenuItem>());
        flyout.Hide();
        return items;
    }

    [AvaloniaFact]
    public void A_tag_rows_delete_button_runs_the_delete_tag_command_for_that_row()
    {
        var dialogs = new ScriptedDialogs();
        using var host = new RefNavigatorTestHost(
            configureRepo: git => git(["tag", "v1"]),
            configureServices: sc => sc.AddSingleton<IDialogService>(dialogs));
        host.ExpandAllSections();

        var tags = host.SectionLists()[2];
        var row = RowWith(tags, r => r is RefTagRowViewModel);
        var button = row.GetVisualDescendants().OfType<Button>().Single();
        Assert.Equal("Delete tag", ToolTip.GetTip(button)?.ToString());
        Assert.Same(host.View.ViewModel.DeleteTagCommand, button.Command);
        Assert.Same(row.DataContext, button.CommandParameter);

        button.Command!.Execute(button.CommandParameter);
        Wait(() => dialogs.DestructiveEffects.Count > 0);

        Assert.Contains("v1", Assert.Single(dialogs.DestructiveEffects));
        Assert.Contains("v1", host.Git("tag", "--list"));
    }

    [AvaloniaFact]
    public void A_stash_rows_menu_offers_its_four_actions_bound_to_the_navigators_commands()
    {
        var id = new GitObjectId(new string('a', 40));
        var stashes = new RecordingStashService
        {
            Entries = [new StashEntry { Index = 0, CommitId = id, Message = "wip", AuthorName = "T", AuthorEmail = "t@e.com", CreatedAt = DateTimeOffset.Now, BranchName = "main" }],
        };
        using var host = new RefNavigatorTestHost(configureServices: sc => sc.AddSingleton<IStashService>(stashes));
        host.View.ViewModel.Stashes.ToggleCommand.Execute(null);
        Wait(() => host.View.ViewModel.Stashes.Rows.OfType<RefStashRowViewModel>().Any());
        host.Settle();

        var row = RowWith(host.SectionLists()[3], r => r is RefStashRowViewModel);
        var items = FlyoutItems(row.GetVisualDescendants().OfType<Button>().Single());

        var vm = host.View.ViewModel;
        Assert.Equal(["Apply", "Apply & drop", "New branch from stash…", "Drop"], items.Select(i => i.Header));
        Assert.Equal<object?>([vm.StashApplyCommand, vm.StashApplyAndDropCommand, vm.StashBranchCommand, vm.StashDropCommand],
            items.Select(i => (object?)i.Command));
        Assert.All(items, i => Assert.Same(row.DataContext, i.Parameter));

        items[0].Command!.Execute(items[0].Parameter);
        Wait(() => stashes.Applied.Count > 0);
        Assert.Equal([id], stashes.Applied);
    }

    [AvaloniaFact]
    public void A_submodule_row_has_the_same_actions_in_its_menu_button_and_its_context_menu()
    {
        var sm = new SubmoduleInfo { Name = "libs/core", Path = "libs/core", Url = "u", Status = SubmoduleStatus.Clean };
        using var host = new RefNavigatorTestHost(configureServices: sc => sc.AddSingleton<ISubmoduleService>(new FixedSubmoduleService([sm])));
        host.View.ViewModel.Submodules.ToggleCommand.Execute(null);
        Wait(() => host.View.ViewModel.Submodules.Rows.OfType<RefSubmoduleRowViewModel>().Any());
        host.Settle();

        var list = host.SectionLists()[4];
        var row = RowWith(list, r => r is RefSubmoduleRowViewModel);
        var vm = host.View.ViewModel;
        Assert.Equal("libs/core", ToolTip.GetTip(row)?.ToString());

        var button = row.GetVisualDescendants().OfType<Button>().Single();
        var fromButton = FlyoutItems(button);
        Assert.Equal(["Init & update", "Sync URL", "Remove"], fromButton.Select(i => i.Header));
        Assert.Equal<object?>([vm.SubmoduleInitAndUpdateCommand, vm.SubmoduleSyncCommand, vm.SubmoduleRemoveCommand],
            fromButton.Select(i => (object?)i.Command));

        var menu = Assert.IsType<ContextMenu>(row.ContextMenu);
        menu.Open(row);
        Dispatcher.UIThread.RunJobs();
        var fromMenu = Snapshot(menu.Items.OfType<MenuItem>());
        menu.Close();
        Assert.Equal(["Init & update", "Sync URL", "Remove"], fromMenu.Select(i => i.Header));
        Assert.Equal<object?>([vm.SubmoduleInitAndUpdateCommand, vm.SubmoduleSyncCommand, vm.SubmoduleRemoveCommand],
            fromMenu.Select(i => (object?)i.Command));
        Assert.All(fromMenu, i => Assert.Same(row.DataContext, i.Parameter));
        Assert.Contains("Update all", list.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
    }

    [AvaloniaFact]
    public void A_worktree_menu_offers_lock_or_unlock_by_state_and_the_main_worktree_has_none()
    {
        var worktrees = new FixedWorktreeService(
        [
            new WorktreeInfo { Path = "/w/main", Branch = "main", IsMainWorktree = true, IsLocked = false },
            new WorktreeInfo { Path = "/w/free", Branch = "free", IsMainWorktree = false, IsLocked = false },
            new WorktreeInfo { Path = "/w/held", Branch = "held", IsMainWorktree = false, IsLocked = true },
        ]);
        using var host = new RefNavigatorTestHost(configureServices: sc => sc.AddSingleton<IWorktreeService>(worktrees));
        host.View.ViewModel.Worktrees.ToggleCommand.Execute(null);
        Wait(() => host.View.ViewModel.Worktrees.Rows.OfType<RefWorktreeRowViewModel>().Any());
        host.Settle();

        var list = host.SectionLists()[5];
        var vm = host.View.ViewModel;
        var main = RowWith(list, r => r is RefWorktreeRowViewModel { Worktree.IsMainWorktree: true });
        var free = RowWith(list, r => r is RefWorktreeRowViewModel { Worktree.Path: "/w/free" });
        var held = RowWith(list, r => r is RefWorktreeRowViewModel { Worktree.Path: "/w/held" });

        Assert.False(main.GetVisualDescendants().OfType<Button>().Single().IsVisible);

        var freeItems = FlyoutItems(free.GetVisualDescendants().OfType<Button>().Single()).Where(i => i.IsVisible).ToList();
        Assert.Equal(["Lock", "Remove"], freeItems.Select(i => i.Header));
        Assert.Equal<object?>([vm.WorktreeLockCommand, vm.WorktreeRemoveCommand], freeItems.Select(i => (object?)i.Command));

        var heldItems = FlyoutItems(held.GetVisualDescendants().OfType<Button>().Single()).Where(i => i.IsVisible).ToList();
        Assert.Equal(["Unlock", "Remove"], heldItems.Select(i => i.Header));
        Assert.Equal<object?>([vm.WorktreeUnlockCommand, vm.WorktreeRemoveCommand], heldItems.Select(i => (object?)i.Command));
    }

    [AvaloniaFact]
    public void A_branch_row_has_the_branch_menu_and_a_double_click_checks_it_out()
    {
        using var host = new RefNavigatorTestHost(configureRepo: git => git(["branch", "topic"]));

        var row = RowWith(host.SectionLists()[0], r => r is RefBranchRowViewModel { Text: "topic" });
        var menu = Assert.IsType<ContextMenu>(row.ContextMenu);
        var headers = ((IEnumerable<Control>)menu.ItemsSource!).OfType<MenuItem>().Select(m => m.Header?.ToString()).ToList();
        Assert.Equal("Checkout", headers[0]);
        Assert.Contains("Delete…", headers);
        Assert.Contains("Copy branch name", headers);

        var press = new PointerPressedEventArgs(row, new Pointer(1, PointerType.Mouse, true), row, new Point(5, 5), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None);
        row.RaiseEvent(new TappedEventArgs(Gestures.DoubleTappedEvent, press) { Source = row });

        Wait(() => host.Git("rev-parse", "--abbrev-ref", "HEAD").Trim() == "topic");
        Assert.Equal("topic", host.Git("rev-parse", "--abbrev-ref", "HEAD").Trim());
    }

    [AvaloniaFact]
    public void A_branch_rows_menu_follows_the_row_it_is_recycled_for()
    {
        using var host = new RefNavigatorTestHost(configureRepo: git => git(["branch", "topic"]));
        var list = host.SectionLists()[0];
        var row = RowWith(list, r => r is RefBranchRowViewModel { Text: "topic" });
        var before = Assert.IsType<ContextMenu>(row.ContextMenu);

        host.Git("branch", "other");
        host.Context.State.RefreshRefsAsync().GetAwaiter().GetResult();
        host.Settle();

        var rows = RefNavigatorTestHost.Rows(list);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.NotNull(r.ContextMenu));
        Assert.NotSame(before, RowWith(list, r => r is RefBranchRowViewModel { Text: "topic" }).ContextMenu);
    }

    [AvaloniaFact]
    public void Opening_a_filter_focuses_its_box()
    {
        using var host = new RefNavigatorTestHost();
        var header = host.SectionHeaders()[0];
        var filter = header.GetVisualDescendants().OfType<Button>().First(b => b.IsVisible && ToolTip.GetTip(b)?.ToString() == "Filter this section");

        filter.Command!.Execute(null);
        Wait(() => host.View.GetVisualDescendants().OfType<TextBox>().Any(t => t.IsFocused));

        var box = host.View.GetVisualDescendants().OfType<TextBox>().Single(t => t.IsEffectivelyVisible);
        Assert.True(box.IsFocused);
        Assert.Equal("Filter…", box.Watermark);
    }
}
