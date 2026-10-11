namespace HistoriaApp.Tests;

/// <summary>Fakes shared by the AI view tests.</summary>
internal static class AiTestSupport
{
    internal sealed class Dialogs : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public bool ConfirmDestructiveResult { get; set; } = true;
        public int ChooseResult { get; set; } = 0;
        public List<(string Title, string Message, bool Destructive)> ConfirmCalls { get; } = [];
        public List<(string Title, string Effect, string ConfirmLabel)> ConfirmDestructiveCalls { get; } = [];
        public List<(string Title, string Message, IReadOnlyList<string> Options, int DefaultIndex)> ChooseCalls { get; } = [];

        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, bool destructive = false, string confirmLabel = "OK",
            bool defaultToCancel = false, DialogIcon icon = DialogIcon.None)
        {
            ConfirmCalls.Add((title, message, destructive));
            return Task.FromResult(ConfirmResult);
        }

        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel)
        {
            ConfirmDestructiveCalls.Add((title, effect, confirmLabel));
            return Task.FromResult(ConfirmDestructiveResult);
        }

        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options, int defaultIndex = 0,
            int destructiveIndex = -1, DialogIcon icon = DialogIcon.None)
        {
            ChooseCalls.Add((title, message, options, defaultIndex));
            return Task.FromResult(ChooseResult);
        }

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

        public Task<string?> PromptAsync(string title, string message, string? defaultValue = null,
            string confirmLabel = "OK", bool primaryConfirm = false) => Task.FromResult<string?>(null);
    }

    internal sealed class Notifications : INotificationService
    {
        public List<Notification> Published { get; } = [];
        public IReadOnlyList<Notification> Current => Published;
        public event EventHandler<NotificationsChangedEventArgs>? Changed;

        public Notification Publish(NotificationSeverity severity, string message, string? details = null,
            IReadOnlyList<NotificationAction>? actions = null)
        {
            var item = new Notification
            {
                Id = Guid.NewGuid(),
                Severity = severity,
                Message = message,
                Details = details,
                CreatedAt = DateTimeOffset.Now,
                Actions = actions ?? [],
            };
            Published.Add(item);
            Changed?.Invoke(this, new NotificationsChangedEventArgs(Published));
            return item;
        }

        public void Dismiss(Guid id) => Published.RemoveAll(n => n.Id == id);
    }

    internal sealed class Client : IAiChatClient
    {
        public List<AiChatRequest> Lists { get; } = [];
        public List<AiChatRequest> Completes { get; } = [];
        public Func<IReadOnlyList<string>> ListResult { get; set; } = () => [];

        public Task<IReadOnlyList<string>> ListModelsAsync(AiChatRequest request, CancellationToken ct)
        {
            Lists.Add(request);
            return Task.FromResult(ListResult());
        }

        public Task<string> CompleteAsync(AiChatRequest request, CancellationToken ct)
        {
            Completes.Add(request);
            return Task.FromResult("feat: x");
        }
    }

    internal sealed class Factory(Client client) : IAiClientFactory
    {
        public int Calls { get; private set; }

        public IAiChatClient For(AiServiceKind kind)
        {
            Calls++;
            return client;
        }
    }

    /// <summary>Waits, pumping the UI thread, until <paramref name="condition"/> holds.</summary>
    internal static async Task PumpUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (condition() || Environment.TickCount64 > deadline)
                break;
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(condition(), "Condition was not met in time.");
    }
}

[Trait("tier", "ui")]
public class AiIntegrationsViewTests
{
    private sealed class Rig
    {
        public InMemorySettingsService Settings { get; } = new();
        public AiProfileService Profiles { get; }
        public AiIntegrationsPanel Panel { get; } = new();
        public AiTestSupport.Dialogs Dialogs { get; } = new();
        public AiTestSupport.Notifications Notifications { get; } = new();
        public AiTestSupport.Client Client { get; } = new();
        public AiTestSupport.Factory Factory { get; }
        public ServiceProvider Services { get; }

        public Rig()
        {
            Profiles = new AiProfileService(Settings);
            Factory = new AiTestSupport.Factory(Client);
            var sc = new ServiceCollection();
            sc.AddSingleton<IAiProfileService>(Profiles);
            sc.AddSingleton<IAiIntegrationsPanel>(Panel);
            sc.AddSingleton<IDialogService>(Dialogs);
            sc.AddSingleton<INotificationService>(Notifications);
            sc.AddSingleton<IAiClientFactory>(Factory);
            Services = sc.BuildServiceProvider();
        }

        public async Task<AiServiceProfile> Add(string name = "", Func<AiServiceProfile, AiServiceProfile>? configure = null)
        {
            var profile = await Profiles.AddAsync();
            await Profiles.UpdateAsync(profile.Id, p => (configure?.Invoke(p) ?? p) with { Name = name == "" ? p.Name : name });
            return Profiles.Profiles.First(p => p.Id == profile.Id);
        }

        public AiIntegrationsView View()
        {
            var view = new AiIntegrationsView(Services);
            Dispatcher.UIThread.RunJobs();
            return view;
        }

        public static void Flush() => Dispatcher.UIThread.RunJobs();
    }

    private static void Commit(TextBox box, string text)
    {
        box.Text = text;
        box.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Profiles_are_numbered_in_the_order_they_were_added()
    {
        var rig = new Rig();
        await rig.Add("Gemini");
        await rig.Add("Work");

        var view = rig.View();

        Assert.Equal(["1. Gemini", "2. Work"], view.RowsForTesting.Select(r => r.Label.Text ?? "").ToArray());
    }

    [AvaloniaFact]
    public async Task Adding_a_profile_appends_and_selects_it()
    {
        var rig = new Rig();
        await rig.Add("First");
        var view = rig.View();

        Click(view.AddButtonForTesting);
        await AiTestSupport.PumpUntil(() => rig.Profiles.Profiles.Count == 2 && view.RowsForTesting.Count == 2);

        Assert.Equal(rig.Profiles.Profiles[1].Id, view.SelectedIdForTesting);
        Assert.Equal("2. Unnamed Service", view.RowsForTesting[1].Label.Text);
    }

    [AvaloniaFact]
    public async Task The_key_box_has_a_red_frame_until_a_key_is_entered()
    {
        var rig = new Rig();
        await rig.Add("A");
        var view = rig.View();
        var editor = view.EditorForTesting!;

        Assert.Equal(new Thickness(2), editor.KeyFrame.BorderThickness);

        Commit(editor.Key, "sk-123");

        Assert.Equal(new Thickness(0), editor.KeyFrame.BorderThickness);
        Assert.Equal("sk-123", rig.Profiles.Profiles.Single().ApiKey);
    }

    [AvaloniaFact]
    public async Task The_red_frame_returns_in_env_mode_when_the_variable_is_unset()
    {
        var rig = new Rig();
        await rig.Add("A", p => p with { ApiKey = "sk-123" });
        var view = rig.View();
        var editor = view.EditorForTesting!;
        Assert.Equal(new Thickness(0), editor.KeyFrame.BorderThickness);

        editor.EnvKey.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        // The saved key text now names an environment variable that doesn't exist.
        Assert.Equal(new Thickness(2), editor.KeyFrame.BorderThickness);
        Assert.True(rig.Profiles.Profiles.Single().ReadApiKeyFromEnv);
    }

    [AvaloniaFact]
    public async Task The_key_is_masked_and_env_mode_reveals_it()
    {
        var rig = new Rig();
        await rig.Add("A", p => p with { ApiKey = "sk-123" });
        var view = rig.View();
        var editor = view.EditorForTesting!;

        Assert.Equal("sk-123", editor.Key.Text);
        Assert.Equal('•', editor.Key.PasswordChar);
        Assert.False(editor.Key.RevealPassword);

        editor.EnvKey.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(editor.Key.RevealPassword);
    }

    [AvaloniaFact]
    public async Task Clicking_a_check_activates_with_no_prompt_and_clicking_the_green_check_deactivates()
    {
        var rig = new Rig();
        var a = await rig.Add("A");
        var view = rig.View();

        Click(view.RowsForTesting[0].Check);
        await AiTestSupport.PumpUntil(() => rig.Profiles.ActiveProfile?.Id == a.Id);
        Assert.Empty(rig.Dialogs.ChooseCalls);
        Assert.Equal("Active", ToolTip.GetTip(view.RowsForTesting[0].Check));

        Click(view.RowsForTesting[0].Check);
        await AiTestSupport.PumpUntil(() => rig.Profiles.ActiveProfile is null);
        Assert.Equal("Make active", ToolTip.GetTip(view.RowsForTesting[0].Check));
    }

    [AvaloniaFact]
    public async Task Switching_the_active_profile_asks_with_Save_as_the_default_option()
    {
        var rig = new Rig();
        var a = await rig.Add("Gemini");
        var b = await rig.Add("Work");
        await rig.Profiles.SetActiveAsync(a.Id);
        var view = rig.View();

        Click(view.RowsForTesting[1].Check);
        await AiTestSupport.PumpUntil(() => rig.Profiles.ActiveProfile?.Id == b.Id);

        var call = Assert.Single(rig.Dialogs.ChooseCalls);
        Assert.Equal("'1. Gemini' is the active AI integration. Switch to '2. Work'?", call.Message);
        Assert.Equal(["Save"], call.Options);
        Assert.Equal(0, call.DefaultIndex);
    }

    [AvaloniaFact]
    public async Task Cancelling_the_switch_keeps_the_old_active_profile()
    {
        var rig = new Rig();
        var a = await rig.Add("Gemini");
        await rig.Add("Work");
        await rig.Profiles.SetActiveAsync(a.Id);
        rig.Dialogs.ChooseResult = -1;
        var view = rig.View();

        Click(view.RowsForTesting[1].Check);
        await AiTestSupport.PumpUntil(() => rig.Dialogs.ChooseCalls.Count == 1);

        Assert.Equal(a.Id, rig.Profiles.ActiveProfile?.Id);
    }

    [AvaloniaFact]
    public async Task Delete_confirms_with_the_destructive_dialog_and_removes_the_profile()
    {
        var rig = new Rig();
        await rig.Add("Gemini");
        var b = await rig.Add("Work");
        await rig.Profiles.SetActiveAsync(b.Id);
        var view = rig.View();
        view.RowsForTesting[1].Row.RaiseEvent(new PointerPressedEventArgs(
            view.RowsForTesting[1].Row, new Pointer(0, PointerType.Mouse, true), view.RowsForTesting[1].Row,
            default, 0, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(b.Id, view.SelectedIdForTesting);

        Click(view.DeleteButtonForTesting);
        await AiTestSupport.PumpUntil(() => rig.Profiles.Profiles.Count == 1);

        var call = Assert.Single(rig.Dialogs.ConfirmDestructiveCalls);
        Assert.Equal("Delete", call.ConfirmLabel);
        Assert.Contains("Delete '2. Work' and its saved API key?", call.Effect);
        Assert.Contains("It is the active integration, so AI commit messages will be turned off.", call.Effect);
        Assert.Contains("This can't be undone.", call.Effect);
        Assert.Null(rig.Profiles.ActiveProfile);
        Assert.Equal(["1. Gemini"], view.RowsForTesting.Select(r => r.Label.Text ?? "").ToArray());
    }

    [AvaloniaFact]
    public async Task Cancelling_the_delete_removes_nothing()
    {
        var rig = new Rig();
        await rig.Add("Only");
        rig.Dialogs.ConfirmDestructiveResult = false;
        var view = rig.View();

        Click(view.DeleteButtonForTesting);
        await AiTestSupport.PumpUntil(() => rig.Dialogs.ConfirmDestructiveCalls.Count == 1);

        Assert.Single(rig.Profiles.Profiles);
    }

    [AvaloniaFact]
    public async Task Choosing_Gemini_fills_the_server_and_leaves_the_key_alone()
    {
        var rig = new Rig();
        await rig.Add("", p => p with { ApiKey = "kept" });
        var view = rig.View();
        var editor = view.EditorForTesting!;

        editor.Template.SelectedIndex = 1;
        await AiTestSupport.PumpUntil(() => rig.Profiles.Profiles.Single().Kind == AiServiceKind.Gemini);

        var saved = rig.Profiles.Profiles.Single();
        Assert.Equal(AiServiceTemplates.GeminiServer, saved.Server);
        Assert.Equal("Gemini", saved.Name);
        Assert.Equal("kept", saved.ApiKey);
        Assert.Equal(AiServiceTemplates.GeminiServer, editor.Server.Text);
        Assert.Empty(rig.Dialogs.ConfirmCalls);
    }

    [AvaloniaFact]
    public async Task Choosing_Gemini_over_a_set_server_asks_first_and_declining_keeps_everything()
    {
        var rig = new Rig();
        await rig.Add("A", p => p with { Server = "https://example.test/v1", Model = "gpt-x" });
        rig.Dialogs.ConfirmResult = false;
        var view = rig.View();
        var editor = view.EditorForTesting!;

        editor.Template.SelectedIndex = 1;
        await AiTestSupport.PumpUntil(() => rig.Dialogs.ConfirmCalls.Count == 1);

        Assert.Equal("Fill in Gemini's connection? Replaces Server and Model.", rig.Dialogs.ConfirmCalls[0].Message);
        Assert.Equal(AiServiceKind.Custom, rig.Profiles.Profiles.Single().Kind);
        Assert.Equal("https://example.test/v1", rig.Profiles.Profiles.Single().Server);
        Assert.Equal(0, editor.Template.SelectedIndex);
    }

    [AvaloniaFact]
    public async Task Constructing_the_view_makes_no_client_call()
    {
        var rig = new Rig();
        var gemini = await rig.Add("G", p => AiServiceTemplates.ApplyGemini(p) with { ApiKey = "k", Model = "gone" });
        await rig.Profiles.SetActiveAsync(gemini.Id);

        var view = rig.View();
        _ = view.EditorForTesting;

        Assert.Equal(0, rig.Factory.Calls);
        Assert.Empty(rig.Client.Lists);
    }

    [AvaloniaFact]
    public async Task The_X_button_closes_the_panel()
    {
        var rig = new Rig();
        rig.Panel.Open();
        var view = rig.View();

        Assert.Equal("X", view.CloseButtonForTesting.Content);
        Assert.Equal("Close", ToolTip.GetTip(view.CloseButtonForTesting));
        Click(view.CloseButtonForTesting);

        Assert.False(rig.Panel.IsOpen);
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task A_loaded_gemini_list_without_the_saved_model_selects_the_replacement_and_notes_it()
    {
        var rig = new Rig();
        await rig.Add("G", p => AiServiceTemplates.ApplyGemini(p) with { ApiKey = "k", Model = "gemini-1.0-pro" });
        rig.Client.ListResult = () => ["gemini-2.0-flash", "gemini-2.5-flash", "gemini-3-flash-preview"];
        var view = rig.View();
        var editor = view.EditorForTesting!;

        Click(editor.ModelRefresh);
        await AiTestSupport.PumpUntil(() => rig.Profiles.Profiles.Single().Model == "gemini-2.5-flash");
        await view.ModelFetchForTesting;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("gemini-2.5-flash", editor.Model.Text);
        Assert.True(editor.ModelNote.IsVisible);
        Assert.Contains("'gemini-1.0-pro' is no longer available", editor.ModelNote.Text);
        Assert.Contains("'gemini-2.5-flash'", editor.ModelNote.Text);
    }

    [AvaloniaFact]
    public async Task A_loaded_gemini_list_that_has_the_saved_model_changes_nothing()
    {
        var rig = new Rig();
        await rig.Add("G", p => AiServiceTemplates.ApplyGemini(p) with { ApiKey = "k", Model = "gemini-2.0-flash" });
        rig.Client.ListResult = () => ["gemini-2.0-flash", "gemini-2.5-flash"];
        var view = rig.View();
        var editor = view.EditorForTesting!;

        Click(editor.ModelRefresh);
        await view.ModelFetchForTesting;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("gemini-2.0-flash", rig.Profiles.Profiles.Single().Model);
        Assert.False(editor.ModelNote.IsVisible);
        Assert.Single(rig.Client.Lists);
    }

    [AvaloniaFact]
    public async Task A_custom_profile_never_switches_its_model_on_its_own()
    {
        var rig = new Rig();
        await rig.Add("C", p => p with { Server = "https://example.test/v1", ApiKey = "k", Model = "old-model" });
        rig.Client.ListResult = () => ["new-a", "new-b"];
        var view = rig.View();
        var editor = view.EditorForTesting!;

        Click(editor.ModelRefresh);
        await view.ModelFetchForTesting;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("old-model", rig.Profiles.Profiles.Single().Model);
        Assert.False(editor.ModelNote.IsVisible);
    }

    [AvaloniaFact]
    public async Task A_failed_model_list_publishes_a_classified_notification()
    {
        var rig = new Rig();
        await rig.Add("C", p => p with { Server = "https://example.test/v1", ApiKey = "k" });
        rig.Client.ListResult = () => throw new AiException(AiErrorKind.Unauthorized, "The AI service rejected the API key.", "HTTP 401");
        var view = rig.View();

        Click(view.EditorForTesting!.ModelRefresh);
        await view.ModelFetchForTesting;
        Dispatcher.UIThread.RunJobs();

        var note = Assert.Single(rig.Notifications.Published);
        Assert.Equal(NotificationSeverity.Error, note.Severity);
        Assert.Contains("rejected the API key", note.Message);
        Assert.Equal("HTTP 401", note.Details);
    }

    [AvaloniaFact]
    public async Task An_unexpected_model_list_failure_is_reported_not_swallowed()
    {
        var rig = new Rig();
        await rig.Add("C", p => p with { Server = "https://example.test/v1", ApiKey = "k", Model = "kept" });
        rig.Client.ListResult = () => throw new InvalidOperationException("odd shape");
        var view = rig.View();
        var editor = view.EditorForTesting!;

        Click(editor.ModelRefresh);
        await view.ModelFetchForTesting;
        Dispatcher.UIThread.RunJobs();

        var note = Assert.Single(rig.Notifications.Published);
        Assert.Equal(NotificationSeverity.Error, note.Severity);
        Assert.Contains("Couldn't load the model list", note.Message);
        Assert.True(editor.Model.IsEnabled);
        Assert.Equal("kept", rig.Profiles.Profiles.Single().Model);
    }

    [AvaloniaFact]
    public async Task Refreshing_with_no_server_asks_for_one_without_a_request()
    {
        var rig = new Rig();
        await rig.Add("C");
        var view = rig.View();

        Click(view.EditorForTesting!.ModelRefresh);

        Assert.Equal(NotificationSeverity.Warning, Assert.Single(rig.Notifications.Published).Severity);
        Assert.Equal(0, rig.Factory.Calls);
    }

    [AvaloniaFact]
    public async Task Committing_a_field_does_not_rebuild_the_editor()
    {
        var rig = new Rig();
        await rig.Add("A");
        var view = rig.View();
        var editor = view.EditorForTesting!;
        var server = editor.Server;

        Commit(editor.Name, "Renamed");

        Assert.Same(editor, view.EditorForTesting);
        Assert.Same(server, view.EditorForTesting!.Server);
        Assert.Equal("1. Renamed", view.RowsForTesting[0].Label.Text);
        Assert.Equal("Renamed", rig.Profiles.Profiles.Single().Name);
    }

    [AvaloniaFact]
    public async Task An_external_model_change_refreshes_the_model_field_but_not_a_focused_field()
    {
        var rig = new Rig();
        var profile = await rig.Add("A", p => AiServiceTemplates.ApplyGemini(p) with { Model = "old" });
        var view = rig.View();
        var editor = view.EditorForTesting!;
        var editor2 = editor;

        await rig.Profiles.UpdateAsync(profile.Id, p => p with { Model = "gemini-2.5-flash" });
        Dispatcher.UIThread.RunJobs();

        Assert.Same(editor2, view.EditorForTesting);
        Assert.Equal("gemini-2.5-flash", editor.Model.Text);
    }

    [AvaloniaFact]
    public async Task Dispose_stops_listening()
    {
        var rig = new Rig();
        await rig.Add("A");
        var view = rig.View();
        view.Dispose();

        await rig.Add("B");
        Dispatcher.UIThread.RunJobs();

        Assert.Single(view.RowsForTesting);
    }

    [AvaloniaFact]
    public async Task Opening_the_panel_on_a_profile_selects_it()
    {
        var rig = new Rig();
        await rig.Add("A");
        var b = await rig.Add("B");
        rig.Panel.Open(b.Id);

        var view = rig.View();

        Assert.Equal(b.Id, view.SelectedIdForTesting);
    }
}
