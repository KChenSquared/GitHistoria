namespace Historia.ViewModels;

/// <summary>
/// What the app band needs from the host that the Core layer cannot reach itself: the developer
/// switches, the About window and quitting the application.
/// </summary>
public interface IAppBandPlatform
{
    /// <summary>True when the app menu offers the Developer entry.</summary>
    bool DeveloperMenuEnabled { get; }

    /// <summary>True when each tab shows its auto-fetch colour bar.</summary>
    bool AutoFetchBarsVisible { get; }

    /// <summary>Raised when <see cref="AutoFetchBarsVisible"/> changes.</summary>
    event Action? AutoFetchBarsVisibleChanged;

    /// <summary>Shows the About window.</summary>
    void ShowAbout();

    /// <summary>Closes the application.</summary>
    void Exit();
}

/// <summary>A host with no developer switches that ignores About and Exit. The default where none is supplied.</summary>
public sealed class NullAppBandPlatform : IAppBandPlatform
{
    public static NullAppBandPlatform Instance { get; } = new();

    private NullAppBandPlatform()
    {
    }

    public bool DeveloperMenuEnabled => false;

    public bool AutoFetchBarsVisible => false;

#pragma warning disable CS0067
    public event Action? AutoFetchBarsVisibleChanged;
#pragma warning restore CS0067

    public void ShowAbout()
    {
    }

    public void Exit()
    {
    }
}
