namespace Historia.Platform;

/// <summary>
/// The modal "About Historia" window: the theme-appropriate ship emblem, the product name, the
/// build version, the third-party credit lines and an OK button. Uses the same chrome as
/// <see cref="SimpleDialog"/> (owner resolution, surface colour) and takes every colour from
/// palette resource keys.
/// </summary>
internal sealed partial class AboutDialog : Window
{
    internal AboutDialog(IThemeService theme, string? informationalVersion)
    {
        InitializeComponent();
        ViewModel = new AboutDialogViewModel(informationalVersion);
        DataContext = ViewModel;
        BindThemedMark(Emblem, theme);
        Closed += (_, _) => ViewModel.Dispose();
    }

    internal AboutDialogViewModel ViewModel { get; }

    /// <summary>The third-party attribution lines shown under the version.</summary>
    internal static IReadOnlyList<string> Credits => AboutDialogViewModel.CreditLines;

    internal static string FormatVersion(string? informational) => AboutDialogViewModel.FormatVersion(informational);

    /// <summary>Shows the About window modally over the main window; completes when it closes.
    /// Safe to call from any thread.</summary>
    public static Task ShowAsync(IThemeService theme) =>
        Dispatcher.UIThread.CheckAccess()
            ? ShowCoreAsync(theme)
            : Dispatcher.UIThread.InvokeAsync(() => ShowCoreAsync(theme));

    private static Task ShowCoreAsync(IThemeService theme) =>
        SimpleDialog.ShowOwnedOrFree(
            new AboutDialog(theme, AboutDialogViewModel.CurrentInformationalVersion()), AvaloniaDialogService.Owner);

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close();
}
