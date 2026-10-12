namespace Historia.Tests;

/// <summary>
/// The shell window's view model: unsaved work blocks the close until the user resolves it, a
/// second close request during the prompt is refused without a second prompt, and a refused
/// prompt leaves the window open for the next attempt.
/// </summary>
public sealed class MainWindowViewModelTests
{
    private sealed class StubWork(string description, bool saves = true) : IUnsavedWork
    {
        public RepositoryContext Context => null!;
        public string Description { get; } = description;
        public int Saved { get; private set; }
        public int Discarded { get; private set; }

        public Task<bool> SaveAsync()
        {
            Saved++;
            return Task.FromResult(saves);
        }

        public void Discard() => Discarded++;
    }

    private sealed class Rig
    {
        public UnsavedWorkRegistry Registry { get; } = new();
        public FakeDialogService Dialogs { get; } = new();
        public MainWindowViewModel Model { get; }

        public Rig(bool withDialogs = true)
            => Model = new MainWindowViewModel(Registry, withDialogs ? Dialogs : null);
    }

    private const int Save = 0;
    private const int Discard = 1;
    private const int Cancel = -1;

    [Fact]
    public void The_status_text_starts_as_ready()
    {
        var model = new MainWindowViewModel();

        Assert.Equal("Ready", model.StatusText);
    }

    [Fact]
    public void Without_unsaved_work_the_window_closes()
    {
        var rig = new Rig();

        Assert.Equal(CloseDecision.Allow, rig.Model.EvaluateClose());
        Assert.Empty(rig.Dialogs.Calls);
    }

    [Fact]
    public void Without_services_the_window_closes()
    {
        var model = new MainWindowViewModel();

        Assert.Equal(CloseDecision.Allow, model.EvaluateClose());
    }

    [Fact]
    public void Without_a_dialog_service_unsaved_work_does_not_block_the_close()
    {
        var rig = new Rig(withDialogs: false);
        rig.Registry.Register(new StubWork("a.txt"));

        Assert.Equal(CloseDecision.Allow, rig.Model.EvaluateClose());
    }

    [Fact]
    public void Unsaved_work_cancels_the_close_and_asks_for_a_prompt()
    {
        var rig = new Rig();
        rig.Registry.Register(new StubWork("a.txt"));

        Assert.Equal(CloseDecision.CancelAndPrompt, rig.Model.EvaluateClose());
    }

    [Fact]
    public async Task Saving_the_unsaved_work_lets_the_next_close_through()
    {
        var rig = new Rig();
        var work = new StubWork("a.txt");
        rig.Registry.Register(work);
        rig.Dialogs.EnqueueChoice(Save);

        Assert.Equal(CloseDecision.CancelAndPrompt, rig.Model.EvaluateClose());
        Assert.True(await rig.Model.ResolveUnsavedWorkAsync());

        Assert.Equal(1, work.Saved);
        Assert.Equal(CloseDecision.Allow, rig.Model.EvaluateClose());
    }

    [Fact]
    public async Task Discarding_the_unsaved_work_lets_the_next_close_through()
    {
        var rig = new Rig();
        var work = new StubWork("a.txt");
        rig.Registry.Register(work);
        rig.Dialogs.EnqueueChoice(Discard);

        rig.Model.EvaluateClose();
        Assert.True(await rig.Model.ResolveUnsavedWorkAsync());

        Assert.Equal(1, work.Discarded);
        Assert.Equal(CloseDecision.Allow, rig.Model.EvaluateClose());
    }

    [Fact]
    public async Task Cancelling_the_prompt_keeps_the_window_open_and_prompts_again_next_time()
    {
        var rig = new Rig();
        var work = new StubWork("a.txt");
        rig.Registry.Register(work);
        rig.Dialogs.EnqueueChoice(Cancel);

        rig.Model.EvaluateClose();
        Assert.False(await rig.Model.ResolveUnsavedWorkAsync());

        Assert.Equal(0, work.Saved);
        Assert.Equal(0, work.Discarded);
        Assert.Equal(CloseDecision.CancelAndPrompt, rig.Model.EvaluateClose());
    }

    [Fact]
    public async Task A_save_that_fails_blocks_the_close()
    {
        var rig = new Rig();
        rig.Registry.Register(new StubWork("a.txt", saves: false));
        rig.Dialogs.EnqueueChoice(Save);

        rig.Model.EvaluateClose();
        Assert.False(await rig.Model.ResolveUnsavedWorkAsync());

        Assert.Equal(CloseDecision.CancelAndPrompt, rig.Model.EvaluateClose());
    }

    [Fact]
    public async Task A_close_request_while_the_prompt_is_open_is_refused_without_a_second_prompt()
    {
        var rig = new Rig();
        rig.Registry.Register(new StubWork("a.txt"));
        rig.Dialogs.EnqueueChoice(Cancel);

        Assert.Equal(CloseDecision.CancelAndPrompt, rig.Model.EvaluateClose());
        Assert.Equal(CloseDecision.CancelOnly, rig.Model.EvaluateClose());
        Assert.Equal(CloseDecision.CancelOnly, rig.Model.EvaluateClose());

        await rig.Model.ResolveUnsavedWorkAsync();

        Assert.Single(rig.Dialogs.Calls, c => c.Method == nameof(IDialogService.ChooseAsync));
    }

    [Fact]
    public async Task The_prompt_names_every_item_that_has_unsaved_work()
    {
        var rig = new Rig();
        rig.Registry.Register(new StubWork("a.txt"));
        rig.Registry.Register(new StubWork("b.txt"));
        rig.Dialogs.EnqueueChoice(Cancel);

        rig.Model.EvaluateClose();
        await rig.Model.ResolveUnsavedWorkAsync();

        var prompt = Assert.Single(rig.Dialogs.Calls);
        Assert.Contains("a.txt", prompt.Detail);
        Assert.Contains("b.txt", prompt.Detail);
    }
}
