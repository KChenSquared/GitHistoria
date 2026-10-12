namespace Historia.Views;

/// <summary>
/// The manage-themes panel, opened from and closing to the Developer panel. The view is markup
/// bound to a <see cref="ThemesViewModel"/>; there is no code here beyond building it.
/// </summary>
public sealed partial class ThemesView : UserControl, IDisposable
{
    public ThemesView(IServiceProvider sp)
    {
        InitializeComponent();
        ViewModel = ActivatorUtilities.CreateInstance<ThemesViewModel>(sp);
        DataContext = ViewModel;
    }

    internal ThemesViewModel ViewModel { get; }

    public void Dispose() => ViewModel.Dispose();
}
