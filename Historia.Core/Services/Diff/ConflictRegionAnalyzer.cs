namespace Historia.Services.Diff;

/// <summary>
/// A highlighted span within a single line.
/// </summary>
public readonly record struct HighlightSpan(int Start, int Length);

/// <summary>
/// Highlight information for a single line in a conflict region.
/// </summary>
public sealed class RegionLineHighlight
{
    public int LineIndex { get; }
    public int DocumentLineIndex { get; }
    public IReadOnlyList<HighlightSpan> Spans { get; }
    public bool ChangedFromBase { get; }

    public RegionLineHighlight(
        int lineIndex,
        int documentLineIndex,
        IReadOnlyList<HighlightSpan> spans,
        bool changedFromBase)
    {
        LineIndex = lineIndex;
        DocumentLineIndex = documentLineIndex;
        Spans = spans;
        ChangedFromBase = changedFromBase;
    }
}

/// <summary>
/// Result of analyzing differences within a conflict region.
/// </summary>
public sealed class ConflictRegionAnalysis
{
    public ConflictRegion Region { get; }
    public IReadOnlyList<RegionLineHighlight> OursHighlights { get; }
    public IReadOnlyList<RegionLineHighlight> TheirsHighlights { get; }
    public IReadOnlyList<RegionLineHighlight>? BaseHighlights { get; }

    public ConflictRegionAnalysis(
        ConflictRegion region,
        IReadOnlyList<RegionLineHighlight> oursHighlights,
        IReadOnlyList<RegionLineHighlight> theirsHighlights,
        IReadOnlyList<RegionLineHighlight>? baseHighlights)
    {
        Region = region;
        OursHighlights = oursHighlights;
        TheirsHighlights = theirsHighlights;
        BaseHighlights = baseHighlights;
    }
}

/// <summary>
/// Analyzes intra-region differences between Ours, Base, and Theirs sections.
/// Pairs lines via DiffPlex and computes intra-line highlights using InlineDiff.
/// </summary>
public static class ConflictRegionAnalyzer
{
    /// <summary>
    /// Analyzes all conflict regions in a parse.
    /// </summary>
    public static IReadOnlyList<ConflictRegionAnalysis> AnalyzeAll(ConflictParse parse)
    {
        if (parse.Regions.Count == 0) return [];
        var list = new List<ConflictRegionAnalysis>(parse.Regions.Count);
        foreach (var r in parse.Regions)
        {
            list.Add(Analyze(r));
        }
        return list;
    }

    /// <summary>
    /// Analyzes the specified conflict region and computes per-line highlight spans.
    /// </summary>
    public static ConflictRegionAnalysis Analyze(ConflictRegion region)
    {
        var oursLines = region.OursLines;
        var theirsLines = region.TheirsLines;
        var baseLines = region.BaseLines;

        var oursSpans = new List<HighlightSpan>[oursLines.Count];
        for (int i = 0; i < oursLines.Count; i++)
        {
            oursSpans[i] = [];
        }

        var theirsSpans = new List<HighlightSpan>[theirsLines.Count];
        for (int i = 0; i < theirsLines.Count; i++)
        {
            theirsSpans[i] = [];
        }

        if (oursLines.Count > 0 && theirsLines.Count > 0)
        {
            string oursJoined = string.Join("\n", oursLines);
            string theirsJoined = string.Join("\n", theirsLines);

            var differ = new Differ();
            var diff = differ.CreateLineDiffs(oursJoined, theirsJoined, ignoreWhitespace: false);

            foreach (var block in diff.DiffBlocks)
            {
                int pairCount = Math.Min(block.DeleteCountA, block.InsertCountB);
                for (int p = 0; p < pairCount; p++)
                {
                    int aIdx = block.DeleteStartA + p;
                    int bIdx = block.InsertStartB + p;
                    string lineA = oursLines[aIdx];
                    string lineB = theirsLines[bIdx];

                    var inlineChanges = InlineDiff.Compare(lineA, lineB);
                    foreach (var change in inlineChanges)
                    {
                        if (change.DeletedCount > 0)
                        {
                            oursSpans[aIdx].Add(new HighlightSpan(change.DeletedStart, change.DeletedCount));
                        }
                        if (change.AddedCount > 0)
                        {
                            theirsSpans[bIdx].Add(new HighlightSpan(change.AddedStart, change.AddedCount));
                        }
                    }
                }

                if (block.DeleteCountA > block.InsertCountB)
                {
                    for (int a = block.DeleteStartA + pairCount; a < block.DeleteStartA + block.DeleteCountA; a++)
                    {
                        if (oursLines[a].Length > 0)
                        {
                            oursSpans[a].Add(new HighlightSpan(0, oursLines[a].Length));
                        }
                    }
                }
                else if (block.InsertCountB > block.DeleteCountA)
                {
                    for (int b = block.InsertStartB + pairCount; b < block.InsertStartB + block.InsertCountB; b++)
                    {
                        if (theirsLines[b].Length > 0)
                        {
                            theirsSpans[b].Add(new HighlightSpan(0, theirsLines[b].Length));
                        }
                    }
                }
            }
        }
        else if (oursLines.Count > 0 && theirsLines.Count == 0)
        {
            for (int i = 0; i < oursLines.Count; i++)
            {
                if (oursLines[i].Length > 0)
                {
                    oursSpans[i].Add(new HighlightSpan(0, oursLines[i].Length));
                }
            }
        }
        else if (theirsLines.Count > 0 && oursLines.Count == 0)
        {
            for (int i = 0; i < theirsLines.Count; i++)
            {
                if (theirsLines[i].Length > 0)
                {
                    theirsSpans[i].Add(new HighlightSpan(0, theirsLines[i].Length));
                }
            }
        }

        HashSet<int> oursChangedFromBase;
        HashSet<int> theirsChangedFromBase;

        if (region.HasBase && baseLines != null)
        {
            oursChangedFromBase = FindChangedLinesFromBase(baseLines, oursLines);
            theirsChangedFromBase = FindChangedLinesFromBase(baseLines, theirsLines);
        }
        else
        {
            oursChangedFromBase = [];
            theirsChangedFromBase = [];
        }

        var oursHighlights = new List<RegionLineHighlight>(oursLines.Count);
        for (int i = 0; i < oursLines.Count; i++)
        {
            oursHighlights.Add(new RegionLineHighlight(
                i,
                region.OursStartLine + i,
                oursSpans[i],
                oursChangedFromBase.Contains(i)));
        }

        var theirsHighlights = new List<RegionLineHighlight>(theirsLines.Count);
        for (int i = 0; i < theirsLines.Count; i++)
        {
            theirsHighlights.Add(new RegionLineHighlight(
                i,
                region.TheirsStartLine + i,
                theirsSpans[i],
                theirsChangedFromBase.Contains(i)));
        }

        List<RegionLineHighlight>? baseHighlights = null;
        if (region.HasBase && baseLines != null)
        {
            baseHighlights = new List<RegionLineHighlight>(baseLines.Count);
            for (int i = 0; i < baseLines.Count; i++)
            {
                baseHighlights.Add(new RegionLineHighlight(
                    i,
                    region.BaseStartLine + i,
                    [],
                    false));
            }
        }

        return new ConflictRegionAnalysis(region, oursHighlights, theirsHighlights, baseHighlights);
    }

    private static HashSet<int> FindChangedLinesFromBase(IReadOnlyList<string> baseLines, IReadOnlyList<string> sideLines)
    {
        var changed = new HashSet<int>();
        if (baseLines.Count == 0)
        {
            for (int i = 0; i < sideLines.Count; i++)
            {
                changed.Add(i);
            }
            return changed;
        }

        if (sideLines.Count == 0)
        {
            return changed;
        }

        string baseText = string.Join("\n", baseLines);
        string sideText = string.Join("\n", sideLines);

        var differ = new Differ();
        var diff = differ.CreateLineDiffs(baseText, sideText, ignoreWhitespace: false);

        foreach (var block in diff.DiffBlocks)
        {
            for (int i = 0; i < block.InsertCountB; i++)
            {
                changed.Add(block.InsertStartB + i);
            }
        }

        return changed;
    }
}
