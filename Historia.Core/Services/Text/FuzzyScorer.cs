namespace Historia.Services.Text;

/// <summary>
/// A span of text defined by inclusive [Start, End) indices.
/// </summary>
public readonly record struct TextSpan(int Start, int End);

/// <summary>
/// Result of a raw fuzzy match containing the score and matched character indices.
/// </summary>
public readonly record struct FuzzyScore(int Score, IReadOnlyList<int> Positions);

/// <summary>
/// Result of scoring an item label against a query, including base score thresholds and match spans.
/// </summary>
public readonly record struct ItemScore(int Score, IReadOnlyList<TextSpan> Matches);

/// <summary>
/// A piece of a search query normalized for fuzzy matching.
/// </summary>
public readonly record struct PreparedQueryPiece(
    string Original,
    string OriginalLowercase,
    string PathNormalized,
    string Normalized,
    string NormalizedLowercase,
    bool ExpectContiguousMatch);

/// <summary>
/// Prepared query containing normalized text and optional space-separated query pieces.
/// </summary>
public readonly record struct PreparedQuery(
    string Original,
    string OriginalLowercase,
    string PathNormalized,
    string Normalized,
    string NormalizedLowercase,
    IReadOnlyList<PreparedQueryPiece>? Values,
    bool ContainsPathSeparator,
    bool ExpectContiguousMatch);

/// <summary>
/// Port of the VS Code quick-open fuzzy scorer (fuzzyScorer.ts and filters.ts).
/// Optimized for path-like ref names with separator bonuses and prefix length boosts.
/// </summary>
public static class FuzzyScorer
{
    public const int LabelPrefixScoreThreshold = 1 << 17;
    public const int LabelScoreThreshold = 1 << 16;

    private const int NoMatch = 0;

    /// <summary>
    /// Computes the fuzzy score and character match positions for a target string against a query.
    /// Historia caps target length at 128 and query length at 64 to bound O(|query|*|target|) allocations.
    /// </summary>
    public static FuzzyScore ScoreFuzzy(string target, string query, string queryLower, bool allowNonContiguousMatches)
    {
        if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(query))
            return new FuzzyScore(NoMatch, Array.Empty<int>());

        // Historia caps target at 128 chars and query at 64 chars to bound quadratic matrix allocation cost.
        if (target.Length > 128)
            target = target[..128];
        if (query.Length > 64)
            query = query[..64];
        if (queryLower.Length > 64)
            queryLower = queryLower[..64];

        int targetLength = target.Length;
        int queryLength = query.Length;

        if (targetLength < queryLength)
            return new FuzzyScore(NoMatch, Array.Empty<int>());

        string targetLower = target.ToLowerInvariant();
        return DoScoreFuzzy(query, queryLower, queryLength, target, targetLower, targetLength, allowNonContiguousMatches);
    }

    public static FuzzyScore ScoreFuzzy(string target, string query, bool allowNonContiguousMatches = true)
    {
        if (string.IsNullOrEmpty(query))
            return new FuzzyScore(NoMatch, Array.Empty<int>());
        return ScoreFuzzy(target, query, query.ToLowerInvariant(), allowNonContiguousMatches);
    }

    private static FuzzyScore DoScoreFuzzy(
        string query,
        string queryLower,
        int queryLength,
        string target,
        string targetLower,
        int targetLength,
        bool allowNonContiguousMatches)
    {
        int[] scores = new int[queryLength * targetLength];
        int[] matches = new int[queryLength * targetLength];

        for (int queryIndex = 0; queryIndex < queryLength; queryIndex++)
        {
            int queryIndexOffset = queryIndex * targetLength;
            int queryIndexPreviousOffset = queryIndexOffset - targetLength;
            bool queryIndexGtNull = queryIndex > 0;

            char queryCharAtIndex = query[queryIndex];
            char queryLowerCharAtIndex = queryLower[queryIndex];

            for (int targetIndex = 0; targetIndex < targetLength; targetIndex++)
            {
                bool targetIndexGtNull = targetIndex > 0;

                int currentIndex = queryIndexOffset + targetIndex;
                int leftIndex = currentIndex - 1;
                int diagIndex = queryIndexPreviousOffset + targetIndex - 1;

                int leftScore = targetIndexGtNull ? scores[leftIndex] : 0;
                int diagScore = queryIndexGtNull && targetIndexGtNull ? scores[diagIndex] : 0;

                int matchesSequenceLength = queryIndexGtNull && targetIndexGtNull ? matches[diagIndex] : 0;

                int score;
                if (diagScore == 0 && queryIndexGtNull)
                {
                    score = 0;
                }
                else
                {
                    score = ComputeCharScore(queryCharAtIndex, queryLowerCharAtIndex, target, targetLower, targetIndex, matchesSequenceLength);
                }

                bool isValidScore = score > 0 && diagScore + score >= leftScore;
                if (isValidScore && (
                    allowNonContiguousMatches ||
                    queryIndexGtNull ||
                    targetLower.AsSpan(targetIndex).StartsWith(queryLower.AsSpan())))
                {
                    matches[currentIndex] = matchesSequenceLength + 1;
                    scores[currentIndex] = diagScore + score;
                }
                else
                {
                    matches[currentIndex] = NoMatch;
                    scores[currentIndex] = leftScore;
                }
            }
        }

        var positions = new List<int>();
        int qIdx = queryLength - 1;
        int tIdx = targetLength - 1;
        while (qIdx >= 0 && tIdx >= 0)
        {
            int currentIndex = qIdx * targetLength + tIdx;
            int match = matches[currentIndex];
            if (match == NoMatch)
            {
                tIdx--;
            }
            else
            {
                positions.Add(tIdx);
                qIdx--;
                tIdx--;
            }
        }

        positions.Reverse();
        return new FuzzyScore(scores[queryLength * targetLength - 1], positions);
    }

    private static int ComputeCharScore(
        char queryCharAtIndex,
        char queryLowerCharAtIndex,
        string target,
        string targetLower,
        int targetIndex,
        int matchesSequenceLength)
    {
        if (!ConsiderAsEqual(queryLowerCharAtIndex, targetLower[targetIndex]))
            return 0;

        int score = 0;

        // Character match bonus
        score += 1;

        // Consecutive match bonus: sequences up to 3 get the full bonus (6)
        // and the remainder gets half the bonus (3).
        if (matchesSequenceLength > 0)
        {
            score += (Math.Min(matchesSequenceLength, 3) * 6) + (Math.Max(0, matchesSequenceLength - 3) * 3);
        }

        // Same case bonus
        if (queryCharAtIndex == target[targetIndex])
        {
            score += 1;
        }

        // Start of word bonus
        if (targetIndex == 0)
        {
            score += 8;
        }
        else
        {
            // After separator bonus
            int separatorBonus = ScoreSeparatorAtPos(target[targetIndex - 1]);
            if (separatorBonus > 0)
            {
                score += separatorBonus;
            }
            // Inside word upper case bonus (camel case) when not in a contiguous sequence.
            else if (IsUpper(target[targetIndex]) && matchesSequenceLength == 0)
            {
                score += 2;
            }
        }

        return score;
    }

    // Historia pins path separators to '/' for ref names, but treats '/' and '\' as equal so backslash queries match.
    private static bool ConsiderAsEqual(char a, char b)
    {
        if (a == b) return true;
        if (a is '/' or '\\') return b is '/' or '\\';
        return false;
    }

    private static int ScoreSeparatorAtPos(char ch)
    {
        return ch switch
        {
            '/' or '\\' => 5,
            '_' or '-' or '.' or ' ' or '\'' or '"' or ':' => 4,
            _ => 0
        };
    }

    private static bool IsUpper(char ch) => ch is >= 'A' and <= 'Z';

    /// <summary>
    /// Checks if target starts with word (case-insensitive).
    /// </summary>
    public static IReadOnlyList<TextSpan>? MatchesPrefix(string word, string wordToMatchAgainst)
    {
        if (string.IsNullOrEmpty(wordToMatchAgainst) || wordToMatchAgainst.Length < word.Length)
            return null;

        if (!wordToMatchAgainst.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            return null;

        return word.Length > 0 ? [new TextSpan(0, word.Length)] : Array.Empty<TextSpan>();
    }

    /// <summary>
    /// Converts individual character match offsets into contiguous TextSpans.
    /// </summary>
    public static IReadOnlyList<TextSpan> CreateMatches(IReadOnlyList<int>? offsets)
    {
        if (offsets == null || offsets.Count == 0)
            return Array.Empty<TextSpan>();

        var ret = new List<TextSpan>();
        TextSpan? last = null;
        for (int i = 0; i < offsets.Count; i++)
        {
            int pos = offsets[i];
            if (last != null && last.Value.End == pos)
            {
                last = new TextSpan(last.Value.Start, pos + 1);
            }
            else
            {
                if (last != null)
                    ret.Add(last.Value);
                last = new TextSpan(pos, pos + 1);
            }
        }
        if (last != null)
            ret.Add(last.Value);

        return ret;
    }

    /// <summary>
    /// Normalizes and merges overlapping or adjacent TextSpans into ascending non-overlapping spans.
    /// </summary>
    public static IReadOnlyList<TextSpan> NormalizeMatches(IReadOnlyList<TextSpan> matches)
    {
        if (matches == null || matches.Count == 0)
            return Array.Empty<TextSpan>();

        var sorted = matches.OrderBy(m => m.Start).ToList();
        var normalized = new List<TextSpan>();
        TextSpan? current = null;

        foreach (var match in sorted)
        {
            if (current == null || !MatchOverlaps(current.Value, match))
            {
                if (current != null)
                    normalized.Add(current.Value);
                current = match;
            }
            else
            {
                current = new TextSpan(
                    Math.Min(current.Value.Start, match.Start),
                    Math.Max(current.Value.End, match.End));
            }
        }
        if (current != null)
            normalized.Add(current.Value);

        return normalized;
    }

    private static bool MatchOverlaps(TextSpan a, TextSpan b)
    {
        if (a.End < b.Start) return false;
        if (b.End < a.Start) return false;
        return true;
    }

    /// <summary>
    /// Prepares a query by normalizing separators, removing noise characters, and splitting into query pieces.
    /// </summary>
    public static PreparedQuery PrepareQuery(string? original)
    {
        original ??= string.Empty;
        string originalLowercase = original.ToLowerInvariant();
        var (pathNormalized, normalized, normalizedLowercase) = NormalizeQuery(original);
        bool containsPathSeparator = pathNormalized.IndexOf('/') >= 0;
        bool expectExactMatch = QueryExpectsExactMatch(original);

        List<PreparedQueryPiece>? values = null;
        string[] originalSplit = original.Split(' ');
        if (originalSplit.Length > 1)
        {
            foreach (string originalPiece in originalSplit)
            {
                bool expectExactMatchPiece = QueryExpectsExactMatch(originalPiece);
                var (pathNormalizedPiece, normalizedPiece, normalizedLowercasePiece) = NormalizeQuery(originalPiece);

                if (!string.IsNullOrEmpty(normalizedPiece))
                {
                    values ??= new List<PreparedQueryPiece>();
                    values.Add(new PreparedQueryPiece(
                        originalPiece,
                        originalPiece.ToLowerInvariant(),
                        pathNormalizedPiece,
                        normalizedPiece,
                        normalizedLowercasePiece,
                        expectExactMatchPiece));
                }
            }
        }

        return new PreparedQuery(
            original,
            originalLowercase,
            pathNormalized,
            normalized,
            normalizedLowercase,
            values,
            containsPathSeparator,
            expectExactMatch);
    }

    private static bool QueryExpectsExactMatch(string query)
    {
        return query.Length >= 2 && query.StartsWith('"') && query.EndsWith('"');
    }

    private static (string PathNormalized, string Normalized, string NormalizedLowercase) NormalizeQuery(string original)
    {
        // Historia pins separator to '/' always (constitution rule 16).
        string pathNormalized = original.Replace('\\', '/');

        var sb = new StringBuilder(pathNormalized.Length);
        foreach (char c in pathNormalized)
        {
            if (c is not '*' and not '\u2026' and not '"' && !char.IsWhiteSpace(c))
            {
                sb.Append(c);
            }
        }

        string normalized = sb.ToString();
        if (normalized.Length > 1 && normalized.EndsWith('#'))
        {
            normalized = normalized[..^1];
        }

        return (pathNormalized, normalized, normalized.ToLowerInvariant());
    }

    /// <summary>
    /// Scores a label against a prepared query, applying prefix boosts and length boosts.
    /// </summary>
    public static ItemScore ScoreItem(string label, PreparedQuery query, bool allowNonContiguousMatches = true)
    {
        if (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(query.Normalized))
            return new ItemScore(NoMatch, Array.Empty<TextSpan>());

        if (query.Values != null && query.Values.Count > 1)
        {
            int totalScore = 0;
            var totalMatches = new List<TextSpan>();

            foreach (var queryPiece in query.Values)
            {
                var pieceScore = ScoreItemPiece(label, queryPiece, allowNonContiguousMatches);
                if (pieceScore.Score == NoMatch)
                    return new ItemScore(NoMatch, Array.Empty<TextSpan>());

                totalScore += pieceScore.Score;
                totalMatches.AddRange(pieceScore.Matches);
            }

            return new ItemScore(totalScore, NormalizeMatches(totalMatches));
        }

        var singlePiece = new PreparedQueryPiece(
            query.Original,
            query.OriginalLowercase,
            query.PathNormalized,
            query.Normalized,
            query.NormalizedLowercase,
            query.ExpectContiguousMatch);

        return ScoreItemPiece(label, singlePiece, allowNonContiguousMatches);
    }

    public static ItemScore ScoreItem(string label, string query, bool allowNonContiguousMatches = true)
    {
        return ScoreItem(label, PrepareQuery(query), allowNonContiguousMatches);
    }

    private static ItemScore ScoreItemPiece(string label, PreparedQueryPiece query, bool allowNonContiguousMatches)
    {
        var (labelScore, labelPositions) = ScoreFuzzy(
            label,
            query.Normalized,
            query.NormalizedLowercase,
            allowNonContiguousMatches && !query.ExpectContiguousMatch);

        if (labelScore <= 0)
            return new ItemScore(NoMatch, Array.Empty<TextSpan>());

        var labelPrefixMatch = MatchesPrefix(query.Normalized, label);
        int baseScore;
        if (labelPrefixMatch != null)
        {
            baseScore = LabelPrefixScoreThreshold;
            int prefixLengthBoost = (int)Math.Round((double)query.Normalized.Length / label.Length * 100);
            baseScore += prefixLengthBoost;
        }
        else
        {
            baseScore = LabelScoreThreshold;
        }

        var matches = labelPrefixMatch ?? CreateMatches(labelPositions);
        return new ItemScore(baseScore + labelScore, matches);
    }
}
