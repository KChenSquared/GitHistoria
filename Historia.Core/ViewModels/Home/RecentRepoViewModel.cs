namespace Historia.ViewModels;

/// <summary>One row of the Home panel's recent-repositories list.</summary>
public sealed partial class RecentRepoViewModel : ObservableObject
{
    private readonly HomeViewModel _host;

    internal RecentRepoViewModel(RecentRepository repository, HomeViewModel host)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _host = host;
        Path = repository.Path;
        DisplayName = repository.DisplayName;
        IsStale = repository.IsStale;
    }

    public string Path { get; }

    public string DisplayName { get; }

    /// <summary>The folder no longer exists, so the row is dimmed and cannot be opened.</summary>
    public bool IsStale { get; }

    /// <summary>The name, marked when the folder is gone.</summary>
    public string Title => DisplayName + (IsStale ? "  (missing)" : "");

    [RelayCommand]
    private Task OpenAsync() => _host.OpenRecentAsync(this);

    [RelayCommand]
    private Task RemoveAsync() => _host.RemoveRecentAsync(this);

    [RelayCommand]
    private void Reveal() => _host.RevealRecent(this);

    [RelayCommand]
    private Task CopyPathAsync() => _host.CopyRecentPathAsync(this);
}
