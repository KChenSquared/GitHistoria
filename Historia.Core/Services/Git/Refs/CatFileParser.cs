namespace Historia.Services.Git;

/// <summary>
/// Parsers for <c>git cat-file --batch-check</c> and <c>--batch</c> output, as
/// <see cref="RefSnapshotProvider"/> uses them when a ref points at a missing object: the types
/// and tag data <c>for-each-ref</c> would have printed, read per object so one missing object
/// can't stop the rest.
/// </summary>
public static class CatFileParser
{
    public const string BatchCheckFormat = "%(objectname) %(objecttype)";

    /// <summary>One line per input, in input order: <c>&lt;sha&gt; &lt;type&gt;</c>, or
    /// <c>&lt;input&gt; missing</c> — returned as a null sha and type. Throws
    /// <see cref="FormatException"/> when the line count doesn't match the input count, so a
    /// short read can never shift every later answer onto the wrong object.</summary>
    public static List<(GitObjectId? Id, string? Type)> ParseBatchCheck(string stdout, int inputCount)
    {
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != inputCount)
            throw new FormatException($"cat-file --batch-check answered {lines.Length} of {inputCount} objects.");

        var result = new List<(GitObjectId?, string?)>(lines.Length);
        foreach (var line in lines)
        {
            var space = line.LastIndexOf(' ');
            if (space <= 0)
                throw new FormatException($"cat-file --batch-check line '{line}' has no type.");
            var type = line[(space + 1)..];
            result.Add(type == "missing"
                ? (null, null)
                : (ForEachRefParser.ParseId(line[..space]) ?? throw new FormatException($"cat-file --batch-check line '{line}' has no object id."), type));
        }
        return result;
    }

    /// <summary>The tagger and message of an annotated tag, as <c>for-each-ref</c>'s
    /// <c>%(taggername)</c>, <c>%(taggerdate:raw)</c> and <c>%(contents)</c> give them: the name
    /// without the email, the date with its own offset, and the whole body after the header —
    /// signature included, bytes as stored.</summary>
    public readonly record struct TagData(string? TaggerName, DateTimeOffset? TaggerWhen, string Message);

    /// <summary>Parses <c>cat-file --batch</c> output (read byte-exact) for tag objects. Each
    /// record is <c>&lt;sha&gt; &lt;type&gt; &lt;size&gt;\n&lt;size bytes&gt;\n</c>; a
    /// <c>&lt;input&gt; missing</c> record is skipped. Throws <see cref="FormatException"/> on a
    /// record that runs past the end of the output.</summary>
    public static Dictionary<GitObjectId, TagData> ParseTagObjects(ReadOnlySpan<byte> stdout)
    {
        var result = new Dictionary<GitObjectId, TagData>();
        var pos = 0;
        while (pos < stdout.Length)
        {
            var eol = stdout[pos..].IndexOf((byte)'\n');
            if (eol < 0)
                throw new FormatException("cat-file --batch header has no line end.");
            var header = Encoding.UTF8.GetString(stdout.Slice(pos, eol)).Split(' ');
            pos += eol + 1;
            if (header is [_, "missing"])
                continue;
            if (header is not [var sha, var type, var sizeText]
                || !int.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                || ForEachRefParser.ParseId(sha) is not { } id)
                throw new FormatException($"cat-file --batch header '{string.Join(' ', header)}' is malformed.");
            if (pos + size > stdout.Length)
                throw new FormatException($"cat-file --batch record for {sha} runs past the end of the output.");

            if (type == "tag")
                result[id] = ParseTag(stdout.Slice(pos, size));
            pos += size;
            if (pos < stdout.Length && stdout[pos] == (byte)'\n')
                pos++;
        }
        return result;
    }

    /// <summary>A tag object is header lines up to the first empty line, then the message.</summary>
    private static TagData ParseTag(ReadOnlySpan<byte> body)
    {
        var split = body.IndexOf("\n\n"u8);
        var headers = split >= 0 ? body[..split] : body;
        var message = split >= 0 ? Encoding.UTF8.GetString(body[(split + 2)..]) : "";

        string? name = null;
        DateTimeOffset? when = null;
        foreach (var line in Encoding.UTF8.GetString(headers).Split('\n'))
        {
            if (!line.StartsWith("tagger ", StringComparison.Ordinal))
                continue;
            var ident = line["tagger ".Length..];
            var open = ident.IndexOf(" <", StringComparison.Ordinal);
            var close = ident.LastIndexOf('>');
            name = open >= 0 ? ident[..open] : ident;
            when = close >= 0 ? ForEachRefParser.ParseRawDate(ident[(close + 1)..]) : null;
            if (name.Length == 0)
                name = null;
            break;
        }
        return new TagData(name, when, message);
    }
}
