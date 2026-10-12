namespace Historia.Views;

/// <summary>Landing page — open / clone / init and the recents list. The view is markup bound to a
/// <see cref="HomeViewModel"/>; the code here hosts the themed emblem, the clone dialog window and
/// the row click.</summary>
public sealed partial class HomeView : UserControl, IDisposable
{
    public HomeView(IServiceProvider sp) : this(sp, "") { }

    /// <param name="intent"><c>"open"</c> / <c>"clone"</c> / <c>"init"</c> from the
    /// app-band <c>+</c> menu — the matching flow is launched once the panel is shown.</param>
    public HomeView(IServiceProvider sp, string intent)
    {
        InitializeComponent();

        var dialogs = sp.GetRequiredService<IDialogService>();
        CloneDialogPresenter showCloneDialog = async (defaultRoot, guessFolderName, profiles, defaultProfileId) =>
        {
            var r = await Historia.Platform.CloneDialog.ShowAsync(dialogs, defaultRoot, guessFolderName, profiles, defaultProfileId);
            return r is null ? null : new CloneDialogChoice(r.Url, r.TargetDirectory, r.Shallow, r.Sparse, r.ProfileId);
        };
        ViewModel = ActivatorUtilities.CreateInstance<HomeViewModel>(
            sp, intent, showCloneDialog, sp.GetService<IUiDispatcher>() ?? new UiDispatcher());
        DataContext = ViewModel;

        ViewKit.BindThemedMark(MarkImage, sp.GetRequiredService<Historia.Services.Theming.IThemeService>());
    }

    internal HomeViewModel ViewModel { get; }

    private void OnRecentPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: RecentRepoViewModel row } control
            && e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            row.OpenCommand.Execute(null);
        }
    }

    public void Dispose() => ViewModel.Dispose();
}

/// <summary>A vertical stack that lays every child out at the width of the widest one, so the rows
/// of the recents list all share one width.</summary>
internal sealed class UniformWidthStackPanel : StackPanel
{
    protected override Size ArrangeOverride(Size finalSize)
    {
        var maxW = 0.0;
        foreach (var child in Children)
        {
            if (child.IsVisible && child.DesiredSize.Width > maxW)
                maxW = child.DesiredSize.Width;
        }
        var width = Math.Max(maxW, finalSize.Width);
        return base.ArrangeOverride(new Size(width, finalSize.Height));
    }
}
