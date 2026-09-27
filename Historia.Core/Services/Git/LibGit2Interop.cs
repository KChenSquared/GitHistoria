namespace Historia.Services.Git;

internal static class LibGit2Interop
{
    public static GitObjectId ToGitObjectId(this ObjectId id) =>
        GitObjectId.FromBytes(id.RawId);

    public static ObjectId ToLibGit2(this GitObjectId id)
    {
        Span<byte> bytes = stackalloc byte[id.HashSize];
        id.CopyTo(bytes);
        return new ObjectId(bytes.ToArray());
    }

    public static ObjectId? ToLibGit2(this GitObjectId? id) =>
        id is null ? null : id.Value.ToLibGit2();
}
