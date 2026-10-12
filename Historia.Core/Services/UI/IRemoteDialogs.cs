namespace Historia.Services.UI;

/// <summary>The values chosen in the Add remote dialog. <paramref name="Fetch"/> is true when the
/// new remote should be fetched right after it is added.</summary>
public sealed record AddRemoteResult(string Name, string Url, bool Fetch);

/// <summary>The values chosen in the Edit remote dialog. <paramref name="ResetPushUrl"/> is true
/// when the user asked to drop the separate push URL.</summary>
public sealed record EditRemoteResult(string Name, string Url, bool ResetPushUrl);

/// <summary>
/// The Add remote and Edit remote dialogs, behind an interface so remote actions can run without a
/// window. Each returns <see langword="null"/> when the user cancels (or, for edit, changes nothing).
/// </summary>
public interface IRemoteDialogs
{
    /// <summary>Asks for a new remote. <paramref name="existing"/> are the repository's remotes: their
    /// names and URLs may not be reused.</summary>
    Task<AddRemoteResult?> ShowAddAsync(IReadOnlyList<RemoteInfo> existing);

    /// <summary>Asks for changes to <paramref name="remote"/>. <paramref name="otherNames"/> are the
    /// names of the repository's other remotes, and <paramref name="urlOwners"/> maps each URL in use
    /// to the remote that owns it.</summary>
    Task<EditRemoteResult?> ShowEditAsync(RemoteInfo remote, IReadOnlyCollection<string> otherNames,
        IReadOnlyDictionary<string, string> urlOwners);
}

/// <summary>Remote dialogs that never open and always answer "cancelled". The default where none is supplied.</summary>
public sealed class NullRemoteDialogs : IRemoteDialogs
{
    public static NullRemoteDialogs Instance { get; } = new();

    private NullRemoteDialogs()
    {
    }

    public Task<AddRemoteResult?> ShowAddAsync(IReadOnlyList<RemoteInfo> existing) => Task.FromResult<AddRemoteResult?>(null);

    public Task<EditRemoteResult?> ShowEditAsync(RemoteInfo remote, IReadOnlyCollection<string> otherNames,
        IReadOnlyDictionary<string, string> urlOwners) => Task.FromResult<EditRemoteResult?>(null);
}
