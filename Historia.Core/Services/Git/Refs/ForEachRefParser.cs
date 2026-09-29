namespace Historia.Services.Git;

/// <summary>
/// The <c>git for-each-ref</c> invocations <see cref="RefSnapshotProvider"/> runs, and the parser
/// for their output. Every field ends in a NUL and a record is exactly a fixed number of fields,
/// so the parser splits on NUL by count — never on newlines, which tag messages contain.
/// The full <see cref="Format"/> asks for the tagger fields and message only when the ref's own
/// object is an annotated tag (<c>%(if:equals=tag)</c>), so a lightweight tag costs no commit
/// read; both branches of the conditional emit the same number of fields.
/// <see cref="ListFormat"/> reads no objects at all, so it still works when a ref points at a
/// missing object — the case where the full format makes git stop.
/// </summary>
public static class ForEachRefParser
{
    public const int FieldCount = 10;

    public const string Format =
        "%(refname)%00%(objectname)%00%(objecttype)%00%(*objectname)%00%(*objecttype)%00%(symref)%00%(upstream:short)%00"
        + "%(if:equals=tag)%(objecttype)%(then)%(taggername)%00%(taggerdate:raw)%00%(contents)%(else)%00%00%(end)%00";

    public static IReadOnlyList<string> Arguments { get; } = ["for-each-ref", "--format=" + Format];

    public const int ListFieldCount = 4;

    public const string ListFormat = "%(refname)%00%(objectname)%00%(symref)%00%(upstream:short)%00";

    public static IReadOnlyList<string> ListArguments { get; } = ["for-each-ref", "--format=" + ListFormat];

    /// <summary>Parses the full <see cref="Format"/>'s stdout, read byte-exact
    /// (<see cref="GitCliRequest.RawStandardOutput"/>), into entries sorted ordinally by name.
    /// Tag messages come back exactly as stored. Throws <see cref="FormatException"/> on a
    /// truncated record rather than returning a partial list that would read as deleted
    /// refs.</summary>
    public static List<RefEntry> Parse(string stdout)
    {
        var result = new List<RefEntry>();
        foreach (var f in Records(stdout, FieldCount))
        {
            var objectType = f[2];
            var annotated = objectType == "tag";
            result.Add(new RefEntry(
                CanonicalName: f[0],
                Target: ParseId(f[1]),
                ObjectType: objectType,
                Peeled: ParseId(f[3]),
                PeeledType: NullIfEmpty(f[4]),
                SymrefTarget: NullIfEmpty(f[5]),
                TaggerName: annotated ? NullIfEmpty(f[7]) : null,
                TaggerWhen: annotated ? ParseRawDate(f[8]) : null,
                Message: annotated ? f[9] : null)
            {
                Upstream = NullIfEmpty(f[6]),
            });
        }

        result.Sort((a, b) => StringComparer.Ordinal.Compare(a.CanonicalName, b.CanonicalName));
        return result;
    }

    /// <summary>Parses <see cref="ListFormat"/>'s stdout: name, object id (as git printed it) and
    /// symref target, in git's order.</summary>
    public static List<(string Name, string ObjectName, string? SymrefTarget, string? Upstream)> ParseList(string stdout) =>
        [.. Records(stdout, ListFieldCount).Select(f => (f[0], f[1], NullIfEmpty(f[2]), NullIfEmpty(f[3])))];

    /// <summary>Splits NUL-terminated fields into records of <paramref name="fieldCount"/>. Git ends
    /// each record with a newline after its last NUL, which lands at the start of the next
    /// record's first field (the ref name — ref names can't contain one).</summary>
    private static IEnumerable<string[]> Records(string stdout, int fieldCount)
    {
        var fields = stdout.Split('\0');
        // The last piece is whatever followed the final NUL: the trailing record newline, or ""
        // for empty output.
        var usable = fields.Length - 1;
        if (!string.IsNullOrWhiteSpace(fields[^1]) || usable % fieldCount != 0)
            throw new FormatException(
                $"for-each-ref output ended mid-record ({usable} fields, not a multiple of {fieldCount}).");

        for (var i = 0; i < usable; i += fieldCount)
        {
            var record = new string[fieldCount];
            Array.Copy(fields, i, record, 0, fieldCount);
            record[0] = record[0].TrimStart('\n');
            if (record[0].Length == 0)
                throw new FormatException($"for-each-ref record {i / fieldCount} has no ref name.");
            yield return record;
        }
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
        // A malformed tag can carry any number; a date DateTimeOffset can't hold reads as none.
        if (seconds < DateTimeOffset.MinValue.ToUnixTimeSeconds() || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            || offset < TimeSpan.FromHours(-14) || offset > TimeSpan.FromHours(14))
            return null;
        var utc = DateTimeOffset.FromUnixTimeSeconds(seconds);
        var localTicks = utc.UtcTicks + offset.Ticks;
        return localTicks >= DateTime.MinValue.Ticks && localTicks <= DateTime.MaxValue.Ticks
            ? utc.ToOffset(offset)
            : null;
    }

    internal static GitObjectId? ParseId(string s) =>
        s.Length is 40 or 64 && GitObjectId.TryParse(s, out var id) ? id : null;

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
