namespace Historia.Services.Git;

/// <summary>
/// Projects <see cref="BranchInfo"/> records from a <see cref="RefSnapshot"/> and known
/// <see cref="RemoteInfo"/> records, without running any extra git process.
/// </summary>
public static class BranchProjection
{
    public static bool IsRemoteHead(BranchInfo b) =>
        b.IsRemote && b.RemoteName is { } r && string.Equals(b.Name, r + "/HEAD", StringComparison.Ordinal);

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
                string? shortName = null;
                if (remote is not null)
                {
                    shortName = rest[(remote.Length + 1)..];
                }
                else
                {
                    var slash = rest.IndexOf('/');
                    if (slash >= 0)
                    {
                        remote = rest[..slash];
                        shortName = rest[(slash + 1)..];
                    }
                }

                // refs/remotes/<remote>/HEAD is a symbolic pointer to the remote's default branch, not a branch.
                if (string.Equals(shortName, "HEAD", StringComparison.Ordinal))
                    continue;

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
