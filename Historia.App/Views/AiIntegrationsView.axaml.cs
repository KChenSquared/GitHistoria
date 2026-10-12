namespace Historia.Views;

/// <summary>
/// The AI Integrations window: the numbered profile list on the left, the selected profile's
/// editor on the right. Shown in the centre pane while <see cref="IAiIntegrationsPanel.IsOpen"/>.
/// The view is markup bound to an <see cref="AiIntegrationsViewModel"/>; the code here is the
/// pointer and key handling that bindings cannot express. Text boxes save when they lose focus,
/// and Enter does the same for the single-line ones.
/// </summary>
public sealed partial class AiIntegrationsView : UserControl, IDisposable
{
    public AiIntegrationsView(IServiceProvider sp)
    {
        InitializeComponent();
        ViewModel = ActivatorUtilities.CreateInstance<AiIntegrationsViewModel>(sp, ViewServices.Dispatcher(sp));
        DataContext = ViewModel;

        this.AddHandler(PointerPressedEvent, SettingsView.OnRootPointerPressedTunnel, RoutingStrategies.Tunnel);
        this.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        this.FindControl<ItemsControl>("RowList")!.AddHandler(PointerPressedEvent, OnRowPressed, RoutingStrategies.Bubble);
    }

    internal AiIntegrationsViewModel ViewModel { get; }

    // A press on a row selects it. A press on the row's check button is handled by the button
    // and never reaches this handler.
    private static void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source)
            return;
        var row = source.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("profile-row"));
        if (row?.DataContext is AiProfileRowViewModel { SelectCommand: var select })
            select.Execute(null);
    }

    // Enter in a single-line box saves it without waiting for the box to lose focus.
    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.Source is TextBox { AcceptsReturn: false } box)
            Avalonia.Data.BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
    }

    public void Dispose() => ViewModel.Dispose();
}
