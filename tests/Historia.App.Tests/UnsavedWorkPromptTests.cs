namespace HistoriaApp.Tests;

public class UnsavedWorkPromptTests
{
    private sealed class StubDialogService : IDialogService
    {
        public int ChooseReturn { get; set; }
        public string? LastTitle { get; private set; }
        public string? LastMessage { get; private set; }
        public IReadOnlyList<string>? LastOptions { get; private set; }
        public int LastDefaultIndex { get; private set; }
        public int LastDestructiveIndex { get; private set; }
        public DialogIcon LastIcon { get; private set; }

        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message, bool destructive = false, string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None) => Task.FromResult(true);
        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel) => Task.FromResult(true);
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
        public Task<string?> PromptAsync(string title, string message, string? defaultValue = null, string confirmLabel = "OK", bool primaryConfirm = false) => Task.FromResult<string?>(null);

        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options, int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None)
        {
            LastTitle = title;
            LastMessage = message;
            LastOptions = options;
            LastDefaultIndex = defaultIndex;
            LastDestructiveIndex = destructiveIndex;
            LastIcon = icon;
            return Task.FromResult(ChooseReturn);
        }
    }

    private sealed class StubWork(string desc, bool saveResult = true) : IUnsavedWork
    {
        public RepositoryContext Context => null!;
        public string Description => desc;
        public bool DiscardCalled { get; private set; }
        public bool SaveCalled { get; private set; }
        public bool SaveResult { get; set; } = saveResult;

        public Task<bool> SaveAsync()
        {
            SaveCalled = true;
            return Task.FromResult(SaveResult);
        }

        public void Discard() => DiscardCalled = true;
    }

    [Fact]
    public async Task Empty_work_list_proceeds_without_prompting()
    {
        var dialogs = new StubDialogService();
        var proceed = await UnsavedWorkPrompt.ResolveAsync([], dialogs);
        Assert.True(proceed);
        Assert.Null(dialogs.LastTitle);
    }

    [Fact]
    public async Task Save_path_saves_all_and_proceeds()
    {
        var dialogs = new StubDialogService { ChooseReturn = 0 }; // Save
        var work1 = new StubWork("file1.cs");
        var work2 = new StubWork("file2.cs");

        var proceed = await UnsavedWorkPrompt.ResolveAsync([work1, work2], dialogs);

        Assert.True(proceed);
        Assert.True(work1.SaveCalled);
        Assert.True(work2.SaveCalled);
        Assert.False(work1.DiscardCalled);
        Assert.False(work2.DiscardCalled);

        // Verify chooser arguments
        Assert.Equal("Unsaved changes", dialogs.LastTitle);
        Assert.Equal(0, dialogs.LastDefaultIndex);
        Assert.Equal(1, dialogs.LastDestructiveIndex);
        Assert.Equal(DialogIcon.Warning, dialogs.LastIcon);
        Assert.NotNull(dialogs.LastOptions);
        Assert.Equal(2, dialogs.LastOptions.Count);
        Assert.Equal("Save", dialogs.LastOptions[0]);
        Assert.Equal("Discard changes", dialogs.LastOptions[1]);
        Assert.Contains("file1.cs", dialogs.LastMessage);
        Assert.Contains("file2.cs", dialogs.LastMessage);
    }

    [Fact]
    public async Task One_failed_save_aborts_and_blocks_close()
    {
        var dialogs = new StubDialogService { ChooseReturn = 0 }; // Save
        var work1 = new StubWork("file1.cs", saveResult: false);
        var work2 = new StubWork("file2.cs", saveResult: true);

        var proceed = await UnsavedWorkPrompt.ResolveAsync([work1, work2], dialogs);

        Assert.False(proceed);
        Assert.True(work1.SaveCalled);
        Assert.False(work2.SaveCalled); // Short-circuits on first failure
    }

    [Fact]
    public async Task Discard_path_discards_each_and_proceeds()
    {
        var dialogs = new StubDialogService { ChooseReturn = 1 }; // Discard changes
        var work1 = new StubWork("file1.cs");
        var work2 = new StubWork("file2.cs");

        var proceed = await UnsavedWorkPrompt.ResolveAsync([work1, work2], dialogs);

        Assert.True(proceed);
        Assert.False(work1.SaveCalled);
        Assert.False(work2.SaveCalled);
        Assert.True(work1.DiscardCalled);
        Assert.True(work2.DiscardCalled);
    }

    [Fact]
    public async Task Cancel_blocks_close_and_performs_no_action()
    {
        var dialogs = new StubDialogService { ChooseReturn = -1 }; // Cancel
        var work1 = new StubWork("file1.cs");
        var work2 = new StubWork("file2.cs");

        var proceed = await UnsavedWorkPrompt.ResolveAsync([work1, work2], dialogs);

        Assert.False(proceed);
        Assert.False(work1.SaveCalled);
        Assert.False(work2.SaveCalled);
        Assert.False(work1.DiscardCalled);
        Assert.False(work2.DiscardCalled);
    }

    [Fact]
    public async Task Chooser_overload_uses_warning_icon_and_destructive_second_option()
    {
        var dialogs = new StubDialogService { ChooseReturn = 0 };
        await UnsavedWorkPrompt.ResolveAsync(dialogs, "Title", "Message", "Save now");

        Assert.Equal(DialogIcon.Warning, dialogs.LastIcon);
        Assert.Equal(0, dialogs.LastDefaultIndex);
        Assert.Equal(1, dialogs.LastDestructiveIndex);
        Assert.NotNull(dialogs.LastOptions);
        Assert.Equal(2, dialogs.LastOptions.Count);
        Assert.Equal("Save now", dialogs.LastOptions[0]);
        Assert.Equal("Discard changes", dialogs.LastOptions[1]);
    }

    [Theory]
    [InlineData(0, UnsavedWorkChoice.Save)]
    [InlineData(1, UnsavedWorkChoice.Discard)]
    [InlineData(-1, UnsavedWorkChoice.Cancel)]
    [InlineData(2, UnsavedWorkChoice.Cancel)]
    public async Task Chooser_overload_maps_choices(int chooseReturn, UnsavedWorkChoice expected)
    {
        var dialogs = new StubDialogService { ChooseReturn = chooseReturn };
        var choice = await UnsavedWorkPrompt.ResolveAsync(dialogs, "Title", "Message", "Save now");
        Assert.Equal(expected, choice);
    }

    [Fact]
    public async Task Chooser_overload_uses_given_title_message_and_labels()
    {
        var dialogs = new StubDialogService { ChooseReturn = 0 };
        await UnsavedWorkPrompt.ResolveAsync(dialogs, "Custom Title", "Custom Message", "Keep", "Drop");

        Assert.Equal("Custom Title", dialogs.LastTitle);
        Assert.Equal("Custom Message", dialogs.LastMessage);
        Assert.NotNull(dialogs.LastOptions);
        Assert.Equal(2, dialogs.LastOptions.Count);
        Assert.Equal("Keep", dialogs.LastOptions[0]);
        Assert.Equal("Drop", dialogs.LastOptions[1]);
    }
}
