namespace Historia.Services.Diff;

/// <summary>
/// Type of a diff display row.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public enum DiffDisplayLineType
{
    None,
    Context,
    Indicator,
    Addition,
    Deletion,
}

/// <summary>
/// Intra-line change highlight span within a diff row.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public readonly record struct InlineHighlight(int Start, int Length);

/// <summary>
/// Single row rendered in combined or side-by-side diff presenters.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class DiffDisplayRow
{
    public required DiffDisplayLineType Type { get; set; }
    public required string Content { get; set; }
    public int? OldLineNumber { get; set; }
    public int? NewLineNumber { get; set; }
    public DiffLineRef? LineRef { get; set; }
    public IReadOnlyList<InlineHighlight> Highlights { get; set; } = [];
    public int HunkIndex { get; set; } = -1;

    public string OldLineText => OldLineNumber is { } n ? n.ToString(CultureInfo.InvariantCulture) : string.Empty;
    public string NewLineText => NewLineNumber is { } n ? n.ToString(CultureInfo.InvariantCulture) : string.Empty;
}

/// <summary>
/// Range of modified lines forming a discrete change block for navigation.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public readonly record struct DiffBlock(int StartLine, int EndLine)
{
    public bool Contains(int line) => line >= StartLine && line <= EndLine;
}

/// <summary>
/// Display-ready diff data model for AvaloniaEdit combined and side-by-side presenters.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public sealed class DiffDisplayModel
{
    public const char CarriageReturnSymbol = '\u240D'; // ␍

    public FileDiff Source { get; }
    public IReadOnlyList<DiffDisplayRow> CombinedRows { get; }
    public IReadOnlyList<DiffDisplayRow> SideBySideOldRows { get; }
    public IReadOnlyList<DiffDisplayRow> SideBySideNewRows { get; }
    public IReadOnlyList<DiffBlock> CombinedBlocks { get; }
    public IReadOnlyList<DiffBlock> SideBySideBlocks { get; }
    public IReadOnlyDictionary<int, DiffLineRef> CombinedRowToRefMap { get; }
    public int MaxLineNumber { get; }

    private DiffDisplayModel(
        FileDiff source,
        IReadOnlyList<DiffDisplayRow> combinedRows,
        IReadOnlyList<DiffDisplayRow> sideBySideOldRows,
        IReadOnlyList<DiffDisplayRow> sideBySideNewRows,
        IReadOnlyList<DiffBlock> combinedBlocks,
        IReadOnlyList<DiffBlock> sideBySideBlocks,
        IReadOnlyDictionary<int, DiffLineRef> combinedRowToRefMap,
        int maxLineNumber)
    {
        Source = source;
        CombinedRows = combinedRows;
        SideBySideOldRows = sideBySideOldRows;
        SideBySideNewRows = sideBySideNewRows;
        CombinedBlocks = combinedBlocks;
        SideBySideBlocks = sideBySideBlocks;
        CombinedRowToRefMap = combinedRowToRefMap;
        MaxLineNumber = maxLineNumber;
    }

    public static string SanitizeLoneCarriageReturn(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        return content.Replace('\r', CarriageReturnSymbol);
    }

    public static string UnescapeCarriageReturn(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        return content.Replace(CarriageReturnSymbol, '\r');
    }

    public static DiffDisplayModel Create(FileDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);

        var combinedRows = new List<DiffDisplayRow>();
        var sideBySideOld = new List<DiffDisplayRow>();
        var sideBySideNew = new List<DiffDisplayRow>();
        var rowToRef = new Dictionary<int, DiffLineRef>();
        int maxLineNumber = 0;

        for (int hi = 0; hi < diff.Hunks.Count; hi++)
        {
            var hunk = diff.Hunks[hi];
            var indicatorRow = new DiffDisplayRow
            {
                Type = DiffDisplayLineType.Indicator,
                Content = SanitizeLoneCarriageReturn(hunk.Header),
                OldLineNumber = null,
                NewLineNumber = null,
                LineRef = null,
                HunkIndex = hi,
            };

            combinedRows.Add(indicatorRow);
            sideBySideOld.Add(indicatorRow);
            sideBySideNew.Add(indicatorRow);

            var deletedRun = new List<DiffDisplayRow>();
            var addedRun = new List<DiffDisplayRow>();

            void FlushRuns()
            {
                if (deletedRun.Count > 0 && addedRun.Count > 0)
                {
                    ComputeInlineHighlights(deletedRun, addedRun);
                }

                foreach (var del in deletedRun)
                {
                    rowToRef[combinedRows.Count] = del.LineRef!.Value;
                    combinedRows.Add(del);
                    sideBySideOld.Add(del);
                }

                foreach (var add in addedRun)
                {
                    rowToRef[combinedRows.Count] = add.LineRef!.Value;
                    combinedRows.Add(add);
                    sideBySideNew.Add(add);
                }

                while (sideBySideOld.Count < sideBySideNew.Count)
                {
                    sideBySideOld.Add(new DiffDisplayRow
                    {
                        Type = DiffDisplayLineType.None,
                        Content = string.Empty,
                        HunkIndex = hi,
                    });
                }

                while (sideBySideNew.Count < sideBySideOld.Count)
                {
                    sideBySideNew.Add(new DiffDisplayRow
                    {
                        Type = DiffDisplayLineType.None,
                        Content = string.Empty,
                        HunkIndex = hi,
                    });
                }

                deletedRun.Clear();
                addedRun.Clear();
            }

            for (int li = 0; li < hunk.Lines.Count; li++)
            {
                var line = hunk.Lines[li];
                var lineRef = new DiffLineRef(hi, li);
                var content = SanitizeLoneCarriageReturn(line.Content);

                if (line.OldLineNumber is { } oldNo)
                {
                    maxLineNumber = Math.Max(maxLineNumber, oldNo);
                }

                if (line.NewLineNumber is { } newNo)
                {
                    maxLineNumber = Math.Max(maxLineNumber, newNo);
                }

                switch (line.Type)
                {
                    case DiffLineType.Deletion:
                        if (addedRun.Count > 0)
                        {
                            FlushRuns();
                        }
                        deletedRun.Add(new DiffDisplayRow
                        {
                            Type = DiffDisplayLineType.Deletion,
                            Content = content,
                            OldLineNumber = line.OldLineNumber,
                            NewLineNumber = null,
                            LineRef = lineRef,
                            HunkIndex = hi,
                        });
                        break;

                    case DiffLineType.Addition:
                        addedRun.Add(new DiffDisplayRow
                        {
                            Type = DiffDisplayLineType.Addition,
                            Content = content,
                            OldLineNumber = null,
                            NewLineNumber = line.NewLineNumber,
                            LineRef = lineRef,
                            HunkIndex = hi,
                        });
                        break;

                    case DiffLineType.Context:
                        FlushRuns();
                        var ctxRow = new DiffDisplayRow
                        {
                            Type = DiffDisplayLineType.Context,
                            Content = content,
                            OldLineNumber = line.OldLineNumber,
                            NewLineNumber = line.NewLineNumber,
                            LineRef = lineRef,
                            HunkIndex = hi,
                        };
                        rowToRef[combinedRows.Count] = lineRef;
                        combinedRows.Add(ctxRow);
                        sideBySideOld.Add(ctxRow);
                        sideBySideNew.Add(ctxRow);
                        break;
                }
            }

            FlushRuns();
        }

        var combinedBlocks = BuildBlocks(combinedRows);
        var sideBySideBlocks = BuildBlocks(sideBySideOld);

        return new DiffDisplayModel(
            diff,
            combinedRows,
            sideBySideOld,
            sideBySideNew,
            combinedBlocks,
            sideBySideBlocks,
            rowToRef,
            maxLineNumber);
    }

    private static void ComputeInlineHighlights(List<DiffDisplayRow> deleted, List<DiffDisplayRow> added)
    {
        if (deleted.Count != added.Count)
        {
            return;
        }

        for (int i = 0; i < deleted.Count; i++)
        {
            var left = deleted[i];
            var right = added[i];

            if (left.Content.Length > 1024 || right.Content.Length > 1024)
            {
                continue;
            }

            var chunks = InlineDiff.Compare(left.Content, right.Content);
            if (chunks.Count > 64)
            {
                continue;
            }

            var leftHl = new List<InlineHighlight>();
            var rightHl = new List<InlineHighlight>();

            foreach (var chunk in chunks)
            {
                if (chunk.DeletedCount > 0)
                {
                    leftHl.Add(new InlineHighlight(chunk.DeletedStart, chunk.DeletedCount));
                }

                if (chunk.AddedCount > 0)
                {
                    rightHl.Add(new InlineHighlight(chunk.AddedStart, chunk.AddedCount));
                }
            }

            left.Highlights = leftHl;
            right.Highlights = rightHl;
        }
    }

    private static IReadOnlyList<DiffBlock> BuildBlocks(IReadOnlyList<DiffDisplayRow> rows)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var blocks = new List<DiffBlock>();
        int lineIdx = 0;
        int blockStart = 0;
        bool inBlock = false;

        foreach (var row in rows)
        {
            lineIdx++;
            if (row.Type is DiffDisplayLineType.Addition or DiffDisplayLineType.Deletion or DiffDisplayLineType.None)
            {
                if (!inBlock)
                {
                    inBlock = true;
                    blockStart = lineIdx;
                }
            }
            else
            {
                if (inBlock)
                {
                    blocks.Add(new DiffBlock(blockStart, lineIdx - 1));
                    inBlock = false;
                }
            }
        }

        if (inBlock)
        {
            blocks.Add(new DiffBlock(blockStart, rows.Count));
        }

        return blocks;
    }

    public IReadOnlyList<DiffLineRef> GetSelectedLinesForCombined(int startLine, int endLine)
    {
        var result = new List<DiffLineRef>();
        int min = Math.Min(startLine, endLine);
        int max = Math.Max(startLine, endLine);

        for (int line = min; line <= max; line++)
        {
            int idx = line - 1;
            if (idx >= 0 && idx < CombinedRows.Count)
            {
                var row = CombinedRows[idx];
                if (row.Type is DiffDisplayLineType.Addition or DiffDisplayLineType.Deletion && row.LineRef is { } r)
                {
                    if (!result.Contains(r))
                    {
                        result.Add(r);
                    }
                }
            }
        }

        return result;
    }

    public IReadOnlyList<DiffLineRef> GetSelectedLinesForSideBySide(int startLine, int endLine, bool oldSide)
    {
        var result = new List<DiffLineRef>();
        int min = Math.Min(startLine, endLine);
        int max = Math.Max(startLine, endLine);

        for (int line = min; line <= max; line++)
        {
            int idx = line - 1;
            if (oldSide)
            {
                if (idx >= 0 && idx < SideBySideOldRows.Count)
                {
                    var oldRow = SideBySideOldRows[idx];
                    if (oldRow.Type == DiffDisplayLineType.Deletion && oldRow.LineRef is { } oldRef)
                    {
                        if (!result.Contains(oldRef))
                        {
                            result.Add(oldRef);
                        }
                    }
                }
            }
            else
            {
                if (idx >= 0 && idx < SideBySideNewRows.Count)
                {
                    var newRow = SideBySideNewRows[idx];
                    if (newRow.Type == DiffDisplayLineType.Addition && newRow.LineRef is { } newRef)
                    {
                        if (!result.Contains(newRef))
                        {
                            result.Add(newRef);
                        }
                    }
                }
            }
        }

        return result;
    }

    public IReadOnlyList<DiffLineRef> GetSelectedLinesForSideBySide(int startLine, int endLine)
        => GetSelectedLinesForSideBySide(startLine, endLine, oldSide: true);
}
