namespace Historia.Views;

/// <summary>
/// Standalone "Profiles &amp; Accounts" panel, reached from the app menu's
/// <c>Default profile ▸ Manage profiles…</c> entry (route <c>profiles</c>). The view is markup bound
/// to a <see cref="ProfilesViewModel"/>; there is no view logic here.
/// </summary>
public sealed partial class ProfilesView : UserControl, IDisposable
{
    public ProfilesView(IServiceProvider sp, string? intent = null)
    {
        InitializeComponent();
        ViewModel = intent is null
            ? ActivatorUtilities.CreateInstance<ProfilesViewModel>(sp)
            : ActivatorUtilities.CreateInstance<ProfilesViewModel>(sp, intent);
        DataContext = ViewModel;
    }

    internal ProfilesViewModel ViewModel { get; }

    public void Dispose() => ViewModel.Dispose();
}
