namespace Historia.Services.Git;

/// <summary>
/// A cheap (milliseconds, no process, no object reads) stand-in for "have any refs changed?":
/// FNV-1a over the size and timestamps of every file git keeps refs in. Git replaces a ref file
/// by writing a lock file and renaming it over the old one, so any ref write changes the file's
/// write time (and <c>packed-refs</c>' creation time); a snapshot keyed on this token is re-read
/// only when a ref really moved.
/// <para>Covered: <c>packed-refs</c>; every file under <c>refs/</c> in the common directory and,
/// for a linked worktree, its own <c>refs/</c>; the contents of <c>HEAD</c>; the stash reflog
/// (older stashes live only there); and, for the reftable backend, the contents of
/// <c>reftable/tables.list</c> (tables are immutable and uniquely named, so the list alone
/// changes on every write).</para>
/// <para>Blind spot: on a filesystem with coarse timestamps (FAT, some network shares) a
/// same-size <c>packed-refs</c> rewrite within the timestamp granularity is not seen until the
/// next change.</para>
/// </summary>
public static class RefStateToken
{
    public readonly record struct State(long Token, string? HeadSymref, string? HeadSha, bool IsReftable);

    public static State Compute(string gitDir)
    {
        var commonDir = ResolveCommonDir(gitDir);
        var h = StableHash.Fnv64OffsetBasis;
        void Feed(string s) => h = StableHash.Fnv1a64Step(h, s);

        FeedFile(Feed, "packed-refs", Path.Combine(commonDir, "packed-refs"), withCreation: true);
        FeedFile(Feed, "config", Path.Combine(commonDir, "config"), withCreation: false);
        FeedTree(Feed, "common:", Path.Combine(commonDir, "refs"));
        if (!PathsEqual(gitDir, commonDir))
            FeedTree(Feed, "worktree:", Path.Combine(gitDir, "refs"));
        FeedFile(Feed, "stash-log", Path.Combine(commonDir, "logs", "refs", "stash"), withCreation: false);

        var tablesList = Path.Combine(commonDir, "reftable", "tables.list");
        var isReftable = File.Exists(tablesList);
        if (isReftable)
            Feed("reftable:" + ReadAllTextOrEmpty(tablesList));

        var head = ReadAllTextOrEmpty(Path.Combine(gitDir, "HEAD")).Trim();
        Feed("HEAD:" + head);

        string? headSymref = null, headSha = null;
        if (head.StartsWith("ref:", StringComparison.Ordinal))
            headSymref = head["ref:".Length..].Trim();
        else if (head.Length is 40 or 64)
            headSha = head;

        return new State(unchecked((long)h), headSymref, headSha, isReftable);
    }

    /// <summary>The directory shared by every worktree of the repo — where <c>packed-refs</c>
    /// and the shared <c>refs/</c> live. A linked worktree's git dir names it in its
    /// <c>commondir</c> file (relative to the worktree git dir, or absolute).</summary>
    public static string ResolveCommonDir(string gitDir)
    {
        var file = Path.Combine(gitDir, "commondir");
        var text = ReadAllTextOrEmpty(file).Trim();
        if (text.Length == 0)
            return gitDir;
        return Path.GetFullPath(Path.IsPathRooted(text) ? text : Path.Combine(gitDir, text));
    }

    private static void FeedFile(Action<string> feed, string label, string path, bool withCreation)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists)
        {
            feed(label + ":-");
            return;
        }
        feed(withCreation
            ? $"{label}:{fi.Length}:{fi.LastWriteTimeUtc.Ticks}:{fi.CreationTimeUtc.Ticks}"
            : $"{label}:{fi.Length}:{fi.LastWriteTimeUtc.Ticks}");
    }

    private static void FeedTree(Action<string> feed, string label, string dir)
    {
        var root = new DirectoryInfo(dir);
        if (!root.Exists)
        {
            feed(label + "-");
            return;
        }

        // FileInfo from a directory enumeration carries the size and times the enumeration
        // already read — no extra stat per file.
        var files = new List<(string Rel, long Len, long Ticks)>();
        try
        {
            foreach (var fi in root.EnumerateFiles("*", SearchOption.AllDirectories))
                files.Add((Path.GetRelativePath(dir, fi.FullName), fi.Length, fi.LastWriteTimeUtc.Ticks));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A ref file renamed away mid-enumeration: make this token unique so the caller
            // rescans rather than trusting a half-read tree.
            feed(label + "!" + Guid.NewGuid().ToString("N"));
            return;
        }

        files.Sort((a, b) => StringComparer.Ordinal.Compare(a.Rel, b.Rel));
        feed(label + files.Count);
        foreach (var (rel, len, ticks) in files)
            feed($"{rel}:{len}:{ticks}");
    }

    private static string ReadAllTextOrEmpty(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "\0" + Guid.NewGuid().ToString("N"); }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
