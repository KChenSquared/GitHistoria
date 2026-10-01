namespace Historia.Services.Git;

/// <summary>
/// Reads git's <c>packed-refs</c> file directly, read-only: each packed ref's object id and, when
/// git recorded one, its peeled id (the <c>^&lt;oid&gt;</c> line after an annotated tag). The
/// header's <c>fully-peeled</c> trait is what makes the absence of a peel line meaningful: with
/// it, git has peeled every ref that can be peeled, so a ref without one is not an annotated tag
/// — unless its peel failed when the file was written (a tag whose object is missing), which only
/// an object read can tell.
/// <para>Any line git itself would reject makes the whole file unreadable here (null): the caller
/// falls back to asking git, never to a partial map. The file is read once, opened shared for
/// read, write and delete, and closed before <see cref="TryRead"/> returns — git replaces it by
/// renaming a new file over it.</para>
/// </summary>
public sealed class PackedRefs
{
    private readonly Dictionary<string, (GitObjectId Target, GitObjectId? Peeled)> _refs;

    internal PackedRefs(Dictionary<string, (GitObjectId, GitObjectId?)> refs, bool fullyPeeled, bool sorted)
    {
        _refs = refs;
        FullyPeeled = fullyPeeled;
        Sorted = sorted;
    }

    /// <summary>The header names the <c>fully-peeled</c> trait.</summary>
    public bool FullyPeeled { get; }

    /// <summary>The header names the <c>sorted</c> trait.</summary>
    public bool Sorted { get; }

    public int Count => _refs.Count;

    /// <summary>Every packed ref with its id and peel, in no particular order.</summary>
    public IEnumerable<(string Name, GitObjectId Target, GitObjectId? Peeled)> Entries =>
        _refs.Select(kv => (kv.Key, kv.Value.Target, kv.Value.Peeled));

    public bool TryGet(string name, out GitObjectId target, out GitObjectId? peeled)
    {
        if (_refs.TryGetValue(name, out var entry))
        {
            (target, peeled) = entry;
            return true;
        }
        target = default;
        peeled = null;
        return false;
    }
}

public static class PackedRefsReader
{
    private const string HeaderPrefix = "# pack-refs with: ";

    /// <summary>The packed refs in <paramref name="commonDir"/>; null when the file is absent,
    /// unreadable, or has a line git's own parser would reject (logged with the reason).</summary>
    public static PackedRefs? TryRead(string commonDir)
    {
        var path = Path.Combine(commonDir, "packed-refs");
        byte[] bytes;
        try
        {
            if (!File.Exists(path))
                return null;
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = RandomAccess.GetLength(handle);
            bytes = new byte[length];
            var done = 0;
            while (done < bytes.Length)
            {
                var n = RandomAccess.Read(handle, bytes.AsSpan(done), done);
                if (n <= 0)
                    throw new IOException("packed-refs shrank while it was read");
                done += n;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Unusable(commonDir, $"I/O error: {ex.Message}");
            return null;
        }

        try
        {
            return Parse(bytes);
        }
        catch (FormatException ex)
        {
            Unusable(commonDir, ex.Message);
            return null;
        }
    }

    private static void Unusable(string commonDir, string reason) =>
        AppLog.Write(AppLogLevel.Info, "git", $"packed-refs not used for {commonDir}: {reason}");

    /// <summary>Parses the file's bytes. Lines are <c>&lt;oid&gt; SP &lt;refname&gt;</c>, each
    /// optionally followed by <c>^&lt;oid&gt;</c>; an optional first line
    /// <c># pack-refs with: &lt;traits&gt;</c>. Every line ends in LF. Throws
    /// <see cref="FormatException"/> on anything else.</summary>
    internal static PackedRefs Parse(ReadOnlySpan<byte> bytes)
    {
        var refs = new Dictionary<string, (GitObjectId, GitObjectId?)>(StringComparer.Ordinal);
        bool fullyPeeled = false, sorted = false;
        var hashHex = 0;
        string? last = null;
        var pos = 0;
        var lineNo = 0;

        while (pos < bytes.Length)
        {
            var eol = bytes[pos..].IndexOf((byte)'\n');
            if (eol < 0)
                throw new FormatException($"line {lineNo + 1} has no line end");
            var line = bytes.Slice(pos, eol);
            pos += eol + 1;
            lineNo++;

            if (lineNo == 1 && line.Length > 0 && line[0] == '#')
            {
                var header = Encoding.UTF8.GetString(line);
                if (!header.StartsWith(HeaderPrefix, StringComparison.Ordinal))
                    throw new FormatException("the first line is a comment but not a pack-refs header");
                var traits = header[HeaderPrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                fullyPeeled = traits.Contains("fully-peeled");
                sorted = traits.Contains("sorted");
                continue;
            }

            if (line.Length > 0 && line[0] == '^')
            {
                if (last is null)
                    throw new FormatException($"line {lineNo}: a peel line with no ref before it");
                var peel = ParseId(line[1..], ref hashHex, lineNo);
                var (target, previous) = refs[last];
                if (previous is not null)
                    throw new FormatException($"line {lineNo}: a second peel line for {last}");
                refs[last] = (target, peel);
                continue;
            }

            var space = line.IndexOf((byte)' ');
            if (space <= 0 || space == line.Length - 1)
                throw new FormatException($"line {lineNo} is not '<oid> <refname>'");
            var id = ParseId(line[..space], ref hashHex, lineNo);
            var name = Encoding.UTF8.GetString(line[(space + 1)..]);
            if (name.AsSpan().IndexOfAny('\0', '\r') >= 0)
                throw new FormatException($"line {lineNo}: the ref name holds a NUL or CR");
            if (!refs.TryAdd(name, (id, null)))
                throw new FormatException($"line {lineNo}: {name} is listed twice");
            last = name;
        }

        return new PackedRefs(refs, fullyPeeled, sorted);
    }

    private static GitObjectId ParseId(ReadOnlySpan<byte> hex, ref int hashHex, int lineNo)
    {
        if (hex.Length is not (40 or 64) || (hashHex != 0 && hex.Length != hashHex))
            throw new FormatException($"line {lineNo}: '{Encoding.ASCII.GetString(hex)}' is not an object id");
        Span<char> chars = stackalloc char[hex.Length];
        for (var i = 0; i < hex.Length; i++)
            chars[i] = (char)hex[i];
        if (!GitObjectId.TryParse(chars, out var id))
            throw new FormatException($"line {lineNo}: '{chars}' is not an object id");
        hashHex = hex.Length;
        return id;
    }
}
