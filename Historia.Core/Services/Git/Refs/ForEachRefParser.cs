namespace Historia.Services.Git;

/// <summary>
/// The <c>git for-each-ref</c> invocation <see cref="RefSnapshotProvider"/> runs, and the parser
/// for its output. Every field ends in a NUL and a record is exactly <see cref="FieldCount"/>
/// fields, so the parser splits on NUL by count — never on newlines, which tag messages contain.
/// The tagger fields and message are asked for only when the ref's own object is an annotated
/// tag (<c>%(if:equals=tag)</c>), so a lightweight tag costs no commit read; both branches of the
/// conditional emit the same number of fields.
/// </summary>
public static class ForEachRefParser
{
    public const int FieldCount = 9;

    public const string Format =
        "%(refname)%00%(objectname)%00%(objecttype)%00%(*objectname)%00%(*objecttype)%00%(symref)%00"
        + "%(if:equals=tag)%(objecttype)%(then)%(taggername)%00%(taggerdate:raw)%00%(contents)%(else)%00%00%(end)%00";

    public static IReadOnlyList<string> Arguments { get; } = ["for-each-ref", "--format=" + Format];

    /// <summary>Parses captured stdout into entries sorted ordinally by name. Git ends each
    /// record with a newline after the last NUL, which lands at the start of the next record's
    /// first field. <see cref="GitCliService"/> captures stdout line by line and rejoins it
    /// with <see cref="Environment.NewLine"/>, so a message's line breaks come back as CRLF and
    /// are folded back to LF here. Throws <see cref="FormatException"/> on a truncated record
    /// rather than returning a partial list that would read as deleted refs.</summary>
    public static List<RefEntry> Parse(string stdout)
    {
        var fields = stdout.Split('\0');
        // The last piece is whatever followed the final NUL: the trailing record newline, or ""
        // for empty output.
        var usable = fields.Length - 1;
        if (!string.IsNullOrWhiteSpace(fields[^1]) || usable % FieldCount != 0)
            throw new FormatException(
                $"for-each-ref output ended mid-record ({usable} fields, not a multiple of {FieldCount}).");

        var result = new List<RefEntry>(usable / FieldCount);
        for (var i = 0; i < usable; i += FieldCount)
        {
            var name = fields[i].TrimStart('\r', '\n');
            if (name.Length == 0)
                throw new FormatException($"for-each-ref record {i / FieldCount} has no ref name.");

            var objectType = fields[i + 2];
            var annotated = objectType == "tag";
            result.Add(new RefEntry(
                CanonicalName: name,
                Target: ParseId(fields[i + 1]),
                ObjectType: objectType,
                Peeled: ParseId(fields[i + 3]),
                PeeledType: NullIfEmpty(fields[i + 4]),
                SymrefTarget: NullIfEmpty(fields[i + 5]),
                TaggerName: annotated ? NullIfEmpty(fields[i + 6]) : null,
                TaggerWhen: annotated ? ParseRawDate(fields[i + 7]) : null,
                Message: annotated ? NormalizeNewlines(fields[i + 8]) : null));
        }

        result.Sort((a, b) => StringComparer.Ordinal.Compare(a.CanonicalName, b.CanonicalName));
        return result;
    }

    /// <summary><c>%(taggerdate:raw)</c> is <c>&lt;unix seconds&gt; &lt;±hhmm&gt;</c>.</summary>
    public static DateTimeOffset? ParseRawDate(string raw)
    {
        var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return null;

        var tz = parts[1];
        if (tz.Length != 5 || (tz[0] != '+' && tz[0] != '-')
            || !int.TryParse(tz.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hh)
            || !int.TryParse(tz.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var mm))
            return null;

        var offset = new TimeSpan(hh, mm, 0);
        if (tz[0] == '-')
            offset = -offset;
        return DateTimeOffset.FromUnixTimeSeconds(seconds).ToOffset(offset);
    }

    private static ObjectId? ParseId(string s) =>
        s.Length is 40 or 64 ? new ObjectId(s) : null;

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static string NormalizeNewlines(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal);
}
