namespace Historia.Views;

/// <summary>
/// The dispatcher and clipboard a view hands to its view model. Both come from the service
/// provider so the registered singletons are the ones in use; a view built without a provider, or
/// with one that registers neither, gets a stateless instance of its own.
/// </summary>
internal static class ViewServices
{
    public static IUiDispatcher Dispatcher(IServiceProvider? sp) =>
        sp?.GetService<IUiDispatcher>() ?? new UiDispatcher();

    public static IClipboardService Clipboard(IServiceProvider? sp) =>
        sp?.GetService<IClipboardService>() ?? new ClipboardService();
}
