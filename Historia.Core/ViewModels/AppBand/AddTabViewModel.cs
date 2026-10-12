namespace Historia.ViewModels;

/// <summary>
/// The "+" button that ends the tab strip: a small menu that opens, clones or initialises a
/// repository by routing the detail pane to Home with the matching intent.
/// </summary>
public sealed partial class AddTabViewModel : ObservableObject
{
    private readonly AppBandViewModel _host;

    internal AddTabViewModel(AppBandViewModel host) => _host = host;

    [RelayCommand]
    private void Open() => _host.RequestRoute("?intent=open");

    [RelayCommand]
    private void Clone() => _host.RequestRoute("?intent=clone");

    [RelayCommand]
    private void Initialize() => _host.RequestRoute("?intent=init");
}
