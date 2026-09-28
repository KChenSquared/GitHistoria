namespace Historia.Services.Git;

/// <summary>
/// Projects <see cref="BranchInfo"/> records from a <see cref="RefSnapshot"/> and known
/// <see cref="RemoteInfo"/> records, without running any extra git process.
/// </summary>
public static class BranchProjection
{
    public static IReadOnlyList<BranchInfo> Project(RefSnapshot snapshot, IReadOnlyList<RemoteInfo> remotes)
    {
        var remoteNames = remotes
            .Select(r => r.Name)
            .OrderByDescending(n => n.Length)
            .ToArray();

        var result = new List<BranchInfo>();

        foreach (var r in snapshot.Refs)
        {
            var name = r.CanonicalName;

            if (name.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                result.Add(new BranchInfo
                {
                    Name = name["refs/heads/".Length..],
                    CanonicalName = name,
                    IsRemote = false,
                    RemoteName = null,
                    IsCurrent = string.Equals(name, snapshot.HeadSymref, StringComparison.Ordinal),
                    TipCommitId = r.PeeledCommit,
                    UpstreamName = r.Upstream,
                });
            }
            else if (name.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                var rest = name["refs/remotes/".Length..];
                var remote = Array.Find(remoteNames, rn => rest.StartsWith(rn + "/", StringComparison.Ordinal));
                if (remote is null)
                {
                    var slash = rest.IndexOf('/');
                    if (slash >= 0)
                        remote = rest[..slash];
                }

                result.Add(new BranchInfo
                {
                    Name = rest,
                    CanonicalName = name,
                    IsRemote = true,
                    RemoteName = remote,
                    IsCurrent = string.Equals(name, snapshot.HeadSymref, StringComparison.Ordinal),
                    TipCommitId = r.PeeledCommit,
                    UpstreamName = null,
                });
            }
        }

        return result;
    }
}
