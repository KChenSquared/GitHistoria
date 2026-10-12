namespace Historia.Services.UI;

/// <summary>The Add remote and Edit remote windows, shown through the existing dialog types.</summary>
public sealed class RemoteDialogs : IRemoteDialogs
{
    public Task<AddRemoteResult?> ShowAddAsync(IReadOnlyList<RemoteInfo> existing)
        => AddRemoteDialog.ShowAsync(existing);

    public Task<EditRemoteResult?> ShowEditAsync(RemoteInfo remote, IReadOnlyCollection<string> otherNames,
        IReadOnlyDictionary<string, string> urlOwners)
        => EditRemoteDialog.ShowAsync(remote, otherNames, urlOwners);
}
