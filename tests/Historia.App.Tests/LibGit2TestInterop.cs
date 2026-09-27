namespace HistoriaApp.Tests;

public static class LibGit2TestInterop
{
    public static GitObjectId ToGitObjectId(this LibGit2Sharp.ObjectId id) =>
        GitObjectId.FromBytes(id.RawId);

    public static LibGit2Sharp.ObjectId ToLibGit2(this GitObjectId id)
    {
        Span<byte> bytes = stackalloc byte[id.HashSize];
        id.CopyTo(bytes);
        return new LibGit2Sharp.ObjectId(bytes.ToArray());
    }

    public static LibGit2Sharp.ObjectId? ToLibGit2(this GitObjectId? id) =>
        id is null ? null : id.Value.ToLibGit2();
}
