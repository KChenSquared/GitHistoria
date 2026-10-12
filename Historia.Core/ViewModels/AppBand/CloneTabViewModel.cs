namespace Historia.ViewModels;

/// <summary>
/// The pending tab shown for the duration of a clone. It is not backed by a repository, so it can
/// be neither focused nor dragged and has no terminal or context menu; its button cancels the
/// clone. A progress tick updates the status text and the button in place.
/// </summary>
public sealed partial class CloneTabViewModel : AppBandItemViewModel
{
    private readonly AppBandViewModel _host;

    internal CloneTabViewModel(AppBandViewModel host, CloneStatus status)
    {
        _host = host;
        TargetDirectory = status.TargetDirectory;
        Name = LeafName(status.TargetDirectory);
        StatusText = status.Describe();
        CanCancel = !status.CancelRequested;
    }

    /// <summary>The target folder's name.</summary>
    public string Name { get; }

    /// <summary>The full target folder, shown as the name's tooltip.</summary>
    public string TargetDirectory { get; }

    /// <summary>What the clone is doing right now.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; }

    /// <summary>False once a cancel has been requested.</summary>
    [ObservableProperty]
    public partial bool CanCancel { get; private set; }

    internal void Update(CloneStatus status)
    {
        StatusText = status.Describe();
        CanCancel = !status.CancelRequested;
    }

    [RelayCommand]
    private void Cancel() => _host.CancelClone();

    private static string LeafName(string targetDirectory)
    {
        var leaf = System.IO.Path.GetFileName(targetDirectory.TrimEnd('/', '\\'));
        return string.IsNullOrEmpty(leaf) ? targetDirectory : leaf;
    }
}
