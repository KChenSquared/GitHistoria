namespace Historia.ViewModels;

/// <summary>
/// The name and URL fields shared by the Add remote and Edit remote dialogs, with live validation.
/// The submit command of each dialog is enabled only while <see cref="Problem"/> is null.
/// </summary>
public abstract partial class RemoteDialogViewModel : ViewModelBase
{
    private readonly IReadOnlyCollection<string> _otherNames;
    private readonly IReadOnlyDictionary<string, string> _otherUrls;

    protected RemoteDialogViewModel(IReadOnlyCollection<string> otherNames, IReadOnlyDictionary<string, string> otherUrls)
    {
        _otherNames = otherNames;
        _otherUrls = otherUrls;
    }

    /// <summary>Raised when the dialog should close, after a submit or a cancel.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The remote name as typed.</summary>
    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>The fetch URL as typed.</summary>
    [ObservableProperty]
    public partial string Url { get; set; }

    /// <summary>Why the current input cannot be submitted, or null when it can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProblem))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    public partial string? Problem { get; private set; }

    /// <summary>Whether the problem is shown to the user: not while the form is still untouched.</summary>
    public bool ShowProblem => Problem is not null && !IsUntouched();

    /// <summary>True when the current input passes validation.</summary>
    public bool IsValid => Problem is null;

    /// <summary>Why <paramref name="name"/> / <paramref name="url"/> cannot be saved, or
    /// <see langword="null"/> when both are acceptable. Shared by the Add and Edit dialogs.
    /// <paramref name="otherNames"/> are the names of the repository's other remotes and
    /// <paramref name="otherUrls"/> maps each of their trimmed fetch URLs to the remote using it
    /// (both compared ordinally).</summary>
    public static string? Validate(string name, string url, IReadOnlyCollection<string> otherNames,
        IReadOnlyDictionary<string, string> otherUrls)
    {
        url = url.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return "Enter a name for the remote.";
        if (name.Any(char.IsWhiteSpace))
            return "A remote name cannot contain whitespace.";
        if (name.StartsWith('-'))
            return "A remote name cannot start with '-'.";
        if (name.StartsWith('/') || name.EndsWith('/'))
            return "A remote name cannot start or end with '/'.";
        if (name.Contains("..", StringComparison.Ordinal))
            return "A remote name cannot contain '..'.";
        if (otherNames.Contains(name, StringComparer.Ordinal))
            return $"A remote named '{name}' already exists.";

        if (string.IsNullOrWhiteSpace(url))
            return "Enter a URL for the remote.";
        if (url.Any(char.IsWhiteSpace))
            return "A remote URL cannot contain whitespace.";
        if (otherUrls.TryGetValue(url, out var owner))
            return $"Remote '{owner}' already uses this URL.";
        return null;
    }

    /// <summary>Maps each remote's trimmed fetch URL to its name, for <see cref="Validate"/>.</summary>
    public static Dictionary<string, string> UrlOwners(IEnumerable<RemoteInfo> remotes)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in remotes)
            owners.TryAdd(r.Url.Trim(), r.Name);
        return owners;
    }

    /// <summary>The name with surrounding whitespace removed.</summary>
    protected string TrimmedName => (Name ?? "").Trim();

    /// <summary>The URL with surrounding whitespace removed.</summary>
    protected string TrimmedUrl => (Url ?? "").Trim();

    /// <summary>True while the input has not been edited into something worth complaining about.</summary>
    protected virtual bool IsUntouched() => false;

    /// <summary>Called after <see cref="Problem"/> was recomputed, so a submit command can refresh.</summary>
    protected virtual void OnValidated()
    {
    }

    protected void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    protected void Revalidate()
    {
        Problem = Validate(TrimmedName, TrimmedUrl, _otherNames, _otherUrls);
        OnPropertyChanged(nameof(ShowProblem));
        OnValidated();
    }

    partial void OnNameChanged(string value) => Revalidate();

    partial void OnUrlChanged(string value) => Revalidate();
}
