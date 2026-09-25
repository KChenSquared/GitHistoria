namespace Historia.Services.Diff;

/// <summary>
/// Represents an intra-line modification span between two strings.
/// Ported from SourceGit's TextInlineChange (MIT License, Copyright (c) 2026 sourcegit).
/// </summary>
public readonly record struct InlineChange(int DeletedStart, int DeletedCount, int AddedStart, int AddedCount);

/// <summary>
/// Intra-line diffing algorithm ported from SourceGit (MIT License, Copyright (c) 2026 sourcegit).
/// Computes word-level and character-level edit spans between two lines of text.
/// </summary>
public static class InlineDiff
{
    private sealed class Chunk(int hash, int start, int size)
    {
        public readonly int Hash = hash;
        public readonly int Start = start;
        public readonly int Size = size;
        public bool Modified;
    }

    private enum Edit
    {
        None,
        DeletedRight,
        DeletedLeft,
        AddedRight,
        AddedLeft,
    }

    private enum CharCategory : byte
    {
        Other,       // default: whitespace, control, punctuation, symbols, etc.
        Letter,      // Ll/Lu/Lt/Lm + digit: ASCII and euro letters (latin, greek, cyrillic, etc.)
        OtherLetter, // Lo: CJK, hiragana, katakana, hangul, Thai, Arabic, etc.
    }

    private sealed class EditResult
    {
        public Edit State;
        public int DeleteStart;
        public int DeleteEnd;
        public int AddStart;
        public int AddEnd;
    }

    private static readonly CharCategory[] s_charCategoryCache = BuildCategoryCache();

    private static CharCategory[] BuildCategoryCache()
    {
        var cache = new CharCategory[65536];
        for (int i = 0; i < 65536; i++)
        {
            var ch = (char)i;
            if (char.GetUnicodeCategory(ch) == UnicodeCategory.OtherLetter)
            {
                cache[i] = CharCategory.OtherLetter;
            }
            else if (char.IsLetterOrDigit(ch))
            {
                cache[i] = CharCategory.Letter;
            }
        }
        return cache;
    }

    private static CharCategory GetCategory(char ch) => s_charCategoryCache[ch];

    /// <summary>
    /// Compares two strings and returns the inline change spans.
    /// </summary>
    public static IReadOnlyList<InlineChange> Compare(string? oldValue, string? newValue)
    {
        oldValue ??= string.Empty;
        newValue ??= string.Empty;

        if (oldValue.Length == 0 && newValue.Length == 0)
        {
            return [];
        }

        var hashes = new Dictionary<string, int>(StringComparer.Ordinal);
        var chunksOld = MakeChunks(hashes, oldValue);
        var chunksNew = MakeChunks(hashes, newValue);
        var sizeOld = chunksOld.Count;
        var sizeNew = chunksNew.Count;
        var max = sizeOld + sizeNew + 2;
        var forward = new int[max];
        var reverse = new int[max];
        CheckModified(chunksOld, 0, sizeOld, chunksNew, 0, sizeNew, forward, reverse);

        var ret = new List<InlineChange>();
        var posOld = 0;
        var posNew = 0;
        int lastIndex = -1;

        do
        {
            while (posOld < sizeOld && posNew < sizeNew && !chunksOld[posOld].Modified && !chunksNew[posNew].Modified)
            {
                posOld++;
                posNew++;
            }

            var beginOld = posOld;
            var beginNew = posNew;
            var countOld = 0;
            var countNew = 0;
            for (; posOld < sizeOld && chunksOld[posOld].Modified; posOld++)
            {
                countOld += chunksOld[posOld].Size;
            }
            for (; posNew < sizeNew && chunksNew[posNew].Modified; posNew++)
            {
                countNew += chunksNew[posNew].Size;
            }

            if (countOld + countNew == 0)
            {
                continue;
            }

            var diff = new InlineChange(
                countOld > 0 ? chunksOld[beginOld].Start : 0,
                countOld,
                countNew > 0 ? chunksNew[beginNew].Start : 0,
                countNew);

            if (lastIndex >= 0)
            {
                var last = ret[lastIndex];
                var midSizeOld = diff.DeletedStart - last.DeletedStart - last.DeletedCount;
                var midSizeNew = diff.AddedStart - last.AddedStart - last.AddedCount;
                if (midSizeOld == 1 && midSizeNew == 1)
                {
                    ret[lastIndex] = new InlineChange(
                        last.DeletedStart,
                        last.DeletedCount + 1 + countOld,
                        last.AddedStart,
                        last.AddedCount + 1 + countNew);
                    continue;
                }
            }

            ret.Add(diff);
            lastIndex = ret.Count - 1;
        } while (posOld < sizeOld && posNew < sizeNew);

        return ret;
    }

    private static List<Chunk> MakeChunks(Dictionary<string, int> hashes, string text)
    {
        var start = 0;
        var size = text.Length;
        var chunks = new List<Chunk>();
        if (size == 0)
        {
            return chunks;
        }

        var prev = GetCategory(text[0]);

        for (var i = 1; i < size; i++)
        {
            var ch = text[i];
            var category = GetCategory(ch);
            if (prev != category || category == CharCategory.Other)
            {
                AddChunk(chunks, hashes, text[start..i], start);
                start = i;
            }
            prev = category;
        }

        if (start < size)
        {
            AddChunk(chunks, hashes, text[start..], start);
        }
        return chunks;
    }

    private static void AddChunk(List<Chunk> chunks, Dictionary<string, int> hashes, string data, int start)
    {
        if (!hashes.TryGetValue(data, out var hash))
        {
            hash = hashes.Count;
            hashes.Add(data, hash);
        }
        chunks.Add(new Chunk(hash, start, data.Length));
    }

    private static void CheckModified(List<Chunk> chunksOld, int startOld, int endOld, List<Chunk> chunksNew, int startNew, int endNew, int[] forward, int[] reverse)
    {
        while (startOld < endOld && startNew < endNew && chunksOld[startOld].Hash == chunksNew[startNew].Hash)
        {
            startOld++;
            startNew++;
        }

        while (startOld < endOld && startNew < endNew && chunksOld[endOld - 1].Hash == chunksNew[endNew - 1].Hash)
        {
            endOld--;
            endNew--;
        }

        var lenOld = endOld - startOld;
        var lenNew = endNew - startNew;
        if (lenOld > 0 && lenNew > 0)
        {
            var rs = CheckModifiedEdit(chunksOld, startOld, endOld, chunksNew, startNew, endNew, forward, reverse);
            if (rs.State == Edit.None)
            {
                return;
            }

            if (rs.State == Edit.DeletedRight && rs.DeleteStart - 1 > startOld)
            {
                chunksOld[--rs.DeleteStart].Modified = true;
            }
            else if (rs.State == Edit.DeletedLeft && rs.DeleteEnd < endOld)
            {
                chunksOld[rs.DeleteEnd++].Modified = true;
            }
            else if (rs.State == Edit.AddedRight && rs.AddStart - 1 > startNew)
            {
                chunksNew[--rs.AddStart].Modified = true;
            }
            else if (rs.State == Edit.AddedLeft && rs.AddEnd < endNew)
            {
                chunksNew[rs.AddEnd++].Modified = true;
            }

            CheckModified(chunksOld, startOld, rs.DeleteStart, chunksNew, startNew, rs.AddStart, forward, reverse);
            CheckModified(chunksOld, rs.DeleteEnd, endOld, chunksNew, rs.AddEnd, endNew, forward, reverse);
        }
        else if (lenOld > 0)
        {
            for (int i = startOld; i < endOld; i++)
            {
                chunksOld[i].Modified = true;
            }
        }
        else if (lenNew > 0)
        {
            for (int i = startNew; i < endNew; i++)
            {
                chunksNew[i].Modified = true;
            }
        }
    }

    private static EditResult CheckModifiedEdit(List<Chunk> chunksOld, int startOld, int endOld, List<Chunk> chunksNew, int startNew, int endNew, int[] forward, int[] reverse)
    {
        var lenOld = endOld - startOld;
        var lenNew = endNew - startNew;
        var max = lenOld + lenNew + 1;
        var half = max / 2;
        var delta = lenOld - lenNew;
        var deltaEven = delta % 2 == 0;
        var rs = new EditResult { State = Edit.None };

        forward[1 + half] = 0;
        reverse[1 + half] = lenOld + 1;

        for (int i = 0; i <= half; i++)
        {
            for (int j = -i; j <= i; j += 2)
            {
                var idx = j + half;
                int o;
                if (j == -i || (j != i && forward[idx - 1] < forward[idx + 1]))
                {
                    o = forward[idx + 1];
                    rs.State = Edit.AddedRight;
                }
                else
                {
                    o = forward[idx - 1] + 1;
                    rs.State = Edit.DeletedRight;
                }

                var n = o - j;
                var startX = o;
                var startY = n;
                while (o < lenOld && n < lenNew && chunksOld[o + startOld].Hash == chunksNew[n + startNew].Hash)
                {
                    o++;
                    n++;
                }

                forward[idx] = o;

                if (!deltaEven && j - delta >= -i + 1 && j - delta <= i - 1)
                {
                    var revIdx = (j - delta) + half;
                    var revOld = reverse[revIdx];
                    int revNew = revOld - j;
                    if (revOld <= o && revNew <= n)
                    {
                        if (i == 0)
                        {
                            rs.State = Edit.None;
                        }
                        else
                        {
                            rs.DeleteStart = startX + startOld;
                            rs.DeleteEnd = o + startOld;
                            rs.AddStart = startY + startNew;
                            rs.AddEnd = n + startNew;
                        }
                        return rs;
                    }
                }
            }

            for (int j = -i; j <= i; j += 2)
            {
                var idx = j + half;
                int o;
                if (j == -i || (j != i && reverse[idx + 1] <= reverse[idx - 1]))
                {
                    o = reverse[idx + 1] - 1;
                    rs.State = Edit.DeletedLeft;
                }
                else
                {
                    o = reverse[idx - 1];
                    rs.State = Edit.AddedLeft;
                }

                var n = o - (j + delta);
                var endX = o;
                var endY = n;
                while (o > 0 && n > 0 && chunksOld[startOld + o - 1].Hash == chunksNew[startNew + n - 1].Hash)
                {
                    o--;
                    n--;
                }

                reverse[idx] = o;

                if (deltaEven && j + delta >= -i && j + delta <= i)
                {
                    var forIdx = (j + delta) + half;
                    var forOld = forward[forIdx];
                    int forNew = forOld - (j + delta);
                    if (forOld >= o && forNew >= n)
                    {
                        if (i == 0)
                        {
                            rs.State = Edit.None;
                        }
                        else
                        {
                            rs.DeleteStart = o + startOld;
                            rs.DeleteEnd = endX + startOld;
                            rs.AddStart = n + startNew;
                            rs.AddEnd = endY + startNew;
                        }
                        return rs;
                    }
                }
            }
        }

        rs.State = Edit.None;
        return rs;
    }
}
