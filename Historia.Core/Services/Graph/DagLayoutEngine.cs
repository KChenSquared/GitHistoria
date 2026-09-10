namespace Historia.Services.Graph;

/// <summary>
/// Pure C# DAG layout algorithm (§7.3). Converts a topologically ordered commit stream
/// (children before parents) into visual rows, lanes, and edges in three conceptual passes:
/// ordered walk → greedy lane assignment + edge emission → stable compaction + color.
/// No LibGit2Sharp object is retained; input is <see cref="RawCommit"/> records.
/// </summary>
public sealed class DagLayoutEngine
{
    /// <summary>Computes a fresh layout for up to <paramref name="maxRows"/> commits.</summary>
    /// <param name="retainCommits">When false, the per-row <see cref="CommitMetadata"/> is not
    /// accumulated in the layout state — the caller is spilling it to a disk-backed store and
    /// will swap that in (§7.6). The built <see cref="GraphLayout.Commits"/> is then an empty
    /// <see cref="ArrayCommitStore"/> until the swap.</param>
    /// <param name="isAncestor">§V10.1 checked-out-branch exception: a full-history ancestry
    /// oracle — <c>isAncestor(a, b)</c> is true when commit <paramref name="a"/> is an ancestor of
    /// (or equal to) commit <c>b</c>. Supplied by <see cref="GraphLayoutService"/> from the live
    /// repo; null on the optimistic stale first paint, where the exception is skipped and the
    /// plain newest-tip-wins order stands until the verified recompute.</param>
    public GraphLayoutResult Compute(
        IReadOnlyList<RawCommit> commits,
        IReadOnlyDictionary<ObjectId, IReadOnlyList<GraphDecorationInput>>? decorations,
        int maxRows,
        int maxLanes = 48,
        CancellationToken ct = default,
        bool retainCommits = true,
        Func<ObjectId, ObjectId, bool>? isAncestor = null)
    {
        var state = new GraphLayoutState { MaxLanes = Math.Max(2, maxLanes), RetainCommits = retainCommits };
        SeedTipPriority(state, commits, decorations, isAncestor);
        Consume(state, commits, decorations, maxRows, ct);
        return Build(state, complete: commits.Count <= maxRows);
    }

    /// <summary>
    /// Resumes the walk from where <paramref name="previous"/> left off, appending up to
    /// <paramref name="additionalRows"/> more rows. Lane indices already assigned are never
    /// renumbered — extension is byte-identical to a full recomputation over the same range
    /// (§7.9.2, criterion 4).
    /// </summary>
    public GraphLayoutResult Extend(
        GraphLayoutResult previous,
        IReadOnlyList<RawCommit> nextCommits,
        IReadOnlyDictionary<ObjectId, IReadOnlyList<GraphDecorationInput>>? decorations,
        int additionalRows,
        CancellationToken ct = default)
    {
        var state = previous.State;
        Consume(state, nextCommits, decorations, state.Rows.Count + additionalRows, ct);
        return Build(state, complete: nextCommits.Count == 0);
    }

    private static void Consume(
        GraphLayoutState s,
        IReadOnlyList<RawCommit> commits,
        IReadOnlyDictionary<ObjectId, IReadOnlyList<GraphDecorationInput>>? decorations,
        int rowCeiling,
        CancellationToken ct)
    {
        foreach (var commit in commits)
        {
            if (s.Rows.Count >= rowCeiling)
                break;
            ct.ThrowIfCancellationRequested();

            var row = s.NextRowIndex++;
            var deco = decorations is not null && decorations.TryGetValue(commit.Id, out var d) ? d : null;

            // ── Determine this commit's raw lane ──
            int rawLane;
            List<Res>? incoming = null;
            if (s.Reservations.TryGetValue(commit.Id, out incoming) && incoming.Count > 0)
            {
                // A deferred merge-parent reservation carries RawLane -1 (§V3-B: its column is
                // not claimed until the parent is actually placed here). Take the lowest real
                // lane; if every incoming reservation is deferred, allocate now.
                var known = int.MaxValue;
                foreach (var res in incoming)
                    if (res.RawLane >= 0 && res.RawLane < known)
                        known = res.RawLane;
                rawLane = known == int.MaxValue ? AllocLane(s) : known;
                RemoveReservationsFor(s, commit.Id, incoming);
            }
            else if (s.TipTargetLane.TryGetValue(commit.Id, out var pinnedLane))
            {
                // §V9-G: this branch tip was pinned by the older-tip-wins pre-pass. The lane is
                // below LaneCeiling and was never handed to FreeLanes, so it is ours to take
                // directly; ReleaseLane recycles it normally once the branch ends or merges.
                rawLane = pinnedLane;
            }
            else
            {
                rawLane = AllocLane(s); // branch tip / orphan root
            }

            var compacted = Compact(s, rawLane);

            // ── Color (seed once per compacted lane) ──
            var color = ResolveColor(s, compacted, deco, commit.Id);

            // ── Emit incoming edges (child → this commit) ──
            if (incoming is not null)
            {
                // A commit that lists the same parent id twice (a self-merge, a malformed
                // octopus) reserves it once per slot; the walk should draw one edge from that
                // child, not one per reservation. One child == one row, so ChildRow dedupes.
                HashSet<int>? seenChildRows = incoming.Count > 1 ? [] : null;
                foreach (var res in incoming)
                {
                    // Any extra (real) lane this commit was reserved in is now free. A deferred
                    // reservation (RawLane -1) never claimed one. Done for every reservation,
                    // including a de-duplicated one.
                    if (res.RawLane >= 0 && res.RawLane != rawLane)
                        ReleaseLane(s, res.RawLane);

                    if (seenChildRows is not null && !seenChildRows.Add(res.ChildRow))
                        continue;

                    // The edge always leaves the CHILD's own lane (§V3-3.4). For a first parent
                    // that lane equals this commit's lane and the line is straight; when the child
                    // sat in a different lane it is a branch-out. A second-or-later parent is a
                    // merge edge into this (merge) commit, and it too starts at the child.
                    var fromLane = Compact(s, res.ChildLane);
                    var type = res.IsFirstParent
                        ? (fromLane == compacted ? EdgeType.Straight : EdgeType.BranchOut)
                        : EdgeType.MergeIn;
                    // §V6-G7: a first-parent edge keeps the child's lane colour, as before. A
                    // MergeIn edge is drawn in the SOURCE (merged-in) branch's colour — this
                    // commit's own lane colour — not the destination (merge commit's) lane.
                    var fromColor = s.LaneColor.GetValueOrDefault(fromLane, color);
                    s.RealEdges.Add(new GraphEdge
                    {
                        FromRow = res.ChildRow,
                        FromLane = fromLane,
                        ToRow = row,
                        ToLane = compacted,
                        Type = type,
                        ColorIndex = type == EdgeType.MergeIn ? color : fromColor,
                        // §V6-G7 fix: the lead-in (NodeToBottom/Full, before the elbow) always
                        // matches fromColor — for MergeIn that's the merge commit's OWN lane
                        // colour, which is what's genuinely still drawn there for this row span
                        // (the first-parent edge continues straight through the same lane).
                        // Only the elbow at ToRow switches to the merged-in branch's colour.
                        LeadColorIndex = fromColor,
                    });
                }
            }

            // ── Row + metadata + decorations ──
            var flags = RowFlags.None;
            if (commit.Metadata.ParentCount == 0) flags |= RowFlags.IsRoot;
            if (commit.Metadata.ParentCount >= 2) flags |= RowFlags.IsMerge;
            if (deco is { Count: > 0 })
            {
                flags |= RowFlags.HasDecorations;
                // §V3-7.2: HEAD is per-badge now (IsHead), but a standalone detached-HEAD badge
                // still uses DecorationType.Head. Either marks the row.
                if (deco.Any(x => x.Type == DecorationType.Head || x.IsHead)) flags |= RowFlags.IsHead;
            }

            s.Rows.Add(new GraphRow
            {
                RowIndex = row,
                LaneIndex = compacted,
                ColorIndex = color,
                ParentCount = commit.Metadata.ParentCount,
                Flags = flags,
            });
            if (s.RetainCommits)
                s.Commits.Add(commit.Metadata);
            s.RowByCommitId[commit.Id] = row;

            if (deco is not null)
                foreach (var x in deco)
                    s.Decorations.Add(new GraphDecoration
                    {
                        RowIndex = row,
                        Type = x.Type,
                        Label = x.Label,
                        ColorIndex = color,
                        IsLocal = x.IsLocal,
                        IsHead = x.IsHead,
                        RemotesOrNull = x.Remotes, // §V3-7.2: copied through, no merging here
                        SiblingsOrNull = x.Siblings,
                    });

            // ── Reserve lanes for parents ──
            if (commit.ParentIds.Count == 0)
            {
                ReleaseLane(s, rawLane);
                continue;
            }

            for (var pi = 0; pi < commit.ParentIds.Count; pi++)
            {
                var parent = commit.ParentIds[pi];
                if (pi == 0)
                {
                    AddReservation(s, parent, new Res(rawLane, rawLane, row, IsFirstParent: true));
                }
                else if (s.Reservations.TryGetValue(parent, out var existing) && existing.Count > 0)
                {
                    // Parent already reserved elsewhere — point the merge edge at that lane if it
                    // is known, else stay deferred. ChildLane is this merge commit's own raw lane.
                    var known = int.MaxValue;
                    foreach (var e in existing)
                        if (e.RawLane >= 0 && e.RawLane < known)
                            known = e.RawLane;
                    AddReservation(s, parent, new Res(known == int.MaxValue ? -1 : known, rawLane, row, IsFirstParent: false));
                }
                else
                {
                    // §V3-B: do NOT allocate a column for a merge parent now. A parent that is far
                    // down the walk — or that turns out to sit on an existing spine — should not
                    // hold a lane in the meantime. -1 = "allocate when the parent is placed".
                    AddReservation(s, parent, new Res(-1, rawLane, row, IsFirstParent: false));
                }
            }
        }
    }

    private static GraphLayoutResult Build(GraphLayoutState s, bool complete)
    {
        var offWindow = RegenerateOffWindow(s);
        var edges = new GraphEdge[s.RealEdges.Count + offWindow.Count];
        s.RealEdges.CopyTo(edges, 0);
        offWindow.CopyTo(edges, s.RealEdges.Count);

        var (finalizedRows, segments, elbows) = DeriveVisuals(s.Rows, edges);

        var layout = new GraphLayout
        {
            Rows = finalizedRows,
            Segments = segments,
            Elbows = elbows,
            Commits = new ArrayCommitStore(s.Commits.ToArray()),
            Edges = edges,
            Decorations = s.Decorations.ToArray(),
            RowCount = s.Rows.Count,
            EdgeCount = edges.Length,
            TotalLaneCount = s.NextCompactedLane,
            IsComplete = complete && s.Reservations.Count == 0,
            RowByCommitId = new Dictionary<ObjectId, int>(s.RowByCommitId),
        };

        return new GraphLayoutResult(layout, s);
    }

    private static List<GraphEdge> RegenerateOffWindow(GraphLayoutState s)
    {
        var offWindow = new List<GraphEdge>();
        var boundaryRow = s.Rows.Count;
        foreach (var (_, list) in s.Reservations)
        {
            foreach (var res in list)
            {
                var fromLane = Compact(s, res.ChildLane);
                var toLane = res.RawLane >= 0 ? Compact(s, res.RawLane) : fromLane;
                var stubColor = res.IsFirstParent || res.RawLane < 0
                    ? s.LaneColor.GetValueOrDefault(fromLane, fromLane % LanePalette.Size)
                    : s.LaneColor.GetValueOrDefault(toLane, toLane % LanePalette.Size);
                offWindow.Add(new GraphEdge
                {
                    FromRow = res.ChildRow,
                    FromLane = fromLane,
                    ToRow = boundaryRow,
                    ToLane = toLane,
                    Type = EdgeType.OffWindow,
                    ColorIndex = stubColor,
                    // An off-window stub never reaches an elbow (DeriveVisuals skips ToRow for
                    // EdgeType.OffWindow) — its only visible segments are the lead-in ones, which
                    // must match the lane they're actually drawn in, same as the real-edge fix.
                    LeadColorIndex = s.LaneColor.GetValueOrDefault(fromLane, fromLane % LanePalette.Size),
                });
            }
        }
        return offWindow;
    }

    private static (GraphRow[] Rows, RowLaneSegment[] Segments, RowElbow[] Elbows) DeriveVisuals(
        List<GraphRow> rawRows, GraphEdge[] edges)
    {
        var rowCount = rawRows.Count;
        if (rowCount == 0)
            return ([], [], []);

        var segCounts = new int[rowCount];
        var elbCounts = new int[rowCount];

        foreach (var edge in edges)
        {
            var fromInWindow = edge.FromRow >= 0 && edge.FromRow < rowCount;

            // §V3-3.2: a merge edge turns out of the merge commit's OWN lane at the merge row and
            // then runs straight down the merged-in branch's lane to the far parent — it does not
            // trail the merge lane all the way down and jog sideways only at the parent (that
            // reads as every merge line "sagging down and to the right"). So the elbow lands on
            // FromRow, and both the pass-through and the terminal segment sit in ToLane.
            var mergeTurn = edge.Type == EdgeType.MergeIn && edge.FromLane != edge.ToLane && fromInWindow;

            if (fromInWindow)
            {
                if (mergeTurn)
                    elbCounts[edge.FromRow]++;
                else
                    segCounts[edge.FromRow]++;
            }

            var passEnd = Math.Min(edge.ToRow, rowCount);
            for (var r = edge.FromRow + 1; r < passEnd; r++)
                segCounts[r]++;

            if (edge.ToRow >= 0 && edge.ToRow < rowCount && edge.Type != EdgeType.OffWindow)
            {
                if (edge.FromLane == edge.ToLane || mergeTurn)
                    segCounts[edge.ToRow]++;
                else
                    elbCounts[edge.ToRow]++;
            }
        }

        var totalSegs = 0;
        var totalElbs = 0;
        var segStarts = new int[rowCount];
        var elbStarts = new int[rowCount];
        for (var r = 0; r < rowCount; r++)
        {
            segStarts[r] = totalSegs;
            totalSegs += segCounts[r];
            elbStarts[r] = totalElbs;
            totalElbs += elbCounts[r];
        }

        var allSegments = new RowLaneSegment[totalSegs];
        var allElbows = new RowElbow[totalElbs];

        var segHeads = (int[])segStarts.Clone();
        var elbHeads = (int[])elbStarts.Clone();

        foreach (var edge in edges)
        {
            // §V6-G7 fix: the lead-in (NodeToBottom/Full, before the edge reaches its parent)
            // is coloured with LeadColorIndex — whatever already occupies edge.FromLane — since a
            // MergeIn edge's lead-in runs through the merge commit's OWN lane, still carrying that
            // lane's own line for the same row span. Only the terminal segment/elbow at ToRow
            // switches to ColorIndex (the merged-in branch's own colour for a MergeIn edge).
            var leadColor = edge.LeadColorIndex;
            var color = edge.ColorIndex;
            var fromInWindow = edge.FromRow >= 0 && edge.FromRow < rowCount;

            // See the matching note in the counting pass: a merge edge bends at FromRow and runs
            // down ToLane. Its whole visible run is then the merged-in branch's own lane, so it is
            // drawn end to end in that branch's colour (ColorIndex) — LeadColorIndex only mattered
            // while the lead-in shared the merge commit's lane, which it no longer does.
            var mergeTurn = edge.Type == EdgeType.MergeIn && edge.FromLane != edge.ToLane && fromInWindow;
            var passLane = mergeTurn ? edge.ToLane : edge.FromLane;
            var passColor = mergeTurn ? color : leadColor;

            if (fromInWindow)
            {
                if (mergeTurn)
                {
                    allElbows[elbHeads[edge.FromRow]++] = new RowElbow
                    {
                        FromLane = edge.FromLane,
                        ToLane = edge.ToLane,
                        ColorIndex = color,
                        Kind = ElbowKind.LeaveBottom,
                    };
                }
                else
                {
                    allSegments[segHeads[edge.FromRow]++] = new RowLaneSegment
                    {
                        Lane = edge.FromLane,
                        ColorIndex = leadColor,
                        Type = LaneSegmentType.NodeToBottom
                    };
                }
            }

            var passEnd = Math.Min(edge.ToRow, rowCount);
            for (var r = edge.FromRow + 1; r < passEnd; r++)
            {
                allSegments[segHeads[r]++] = new RowLaneSegment
                {
                    Lane = passLane,
                    ColorIndex = passColor,
                    Type = LaneSegmentType.Full
                };
            }

            if (edge.ToRow >= 0 && edge.ToRow < rowCount && edge.Type != EdgeType.OffWindow)
            {
                if (edge.FromLane == edge.ToLane || mergeTurn)
                {
                    allSegments[segHeads[edge.ToRow]++] = new RowLaneSegment
                    {
                        Lane = edge.ToLane,
                        ColorIndex = color,
                        Type = LaneSegmentType.TopToNode
                    };
                }
                else
                {
                    allElbows[elbHeads[edge.ToRow]++] = new RowElbow
                    {
                        FromLane = edge.FromLane,
                        ToLane = edge.ToLane,
                        ColorIndex = color,
                        Kind = ElbowKind.EnterTop,
                    };
                }
            }
        }

        var finalizedRows = new GraphRow[rowCount];
        for (var r = 0; r < rowCount; r++)
        {
            var raw = rawRows[r];
            finalizedRows[r] = raw with
            {
                SegmentStart = segStarts[r],
                SegmentCount = segCounts[r],
                ElbowStart = elbStarts[r],
                ElbowCount = elbCounts[r],
            };
        }

        return (finalizedRows, allSegments, allElbows);
    }

    // ── Lane allocation ──

    /// <summary>
    /// §V10.1 "newest tip wins the trunk, with a checked-out-branch exception". When the walk
    /// window holds two or more branch tips that are leaves (nothing in the window builds on
    /// them), rank them and pin each to a raw lane in that order; lane 0 is the trunk. Row order
    /// is untouched — the newest commit is still row 0, a tip just moves to a higher lane.
    ///
    /// Base order: <b>newest tip first</b>, by committer date (SHA breaks an exact-timestamp tie
    /// so the order is stable per run).
    ///
    /// Exception: the currently checked-out local branch stays on the trunk when its work has
    /// been built on. Let <c>H</c> be that branch's tip among the candidates. If <c>H</c> is not
    /// already newest, scan the tips newer than <c>H</c> newest-first for the first one <c>H</c>
    /// is an ancestor of:
    /// <list type="bullet">
    ///   <item>ancestor of the newest tip → order is unchanged.</item>
    ///   <item>ancestor of a later (non-newest) tip <c>T</c> → <c>T</c> takes the trunk and every
    ///     tip ahead of it shifts one lane right.</item>
    ///   <item>ancestor of none of the newer tips → <c>H</c> takes the trunk and every other tip
    ///     shifts one lane right.</item>
    /// </list>
    /// The exception is skipped when no ancestry oracle is supplied (the optimistic stale first
    /// paint); the verified recompute that follows applies it.
    ///
    /// It ranks only tips within the first <see cref="TipPriorityScanHorizon"/> commits of the
    /// list the engine is handed, and is a pure function of that prefix (plus the oracle) — never
    /// of <c>maxRows</c>. So a partial <see cref="Compute"/> + <see cref="Extend"/> seeds the same
    /// pins as a full compute over the same list and criterion 4 (§7.9.2) still holds. A
    /// checked-out branch whose tip is beyond the horizon is not a candidate, so the exception
    /// does not fire for it.
    ///
    /// Only the trunk tip and tips whose commit sits in the first <see cref="PinLaneHorizon"/>
    /// rows are actually <b>pinned</b> to a reserved lane. A tip hundreds of rows down does not
    /// need an empty column held for it from row 0 — it gets its lane from the normal allocator
    /// when the walk reaches it. This keeps the near-HEAD region only as wide as the branches
    /// that truly live there (a repo with dozens of branch tips otherwise renders that many empty
    /// lanes across the top, pushing every early merge-in far to the right).
    /// </summary>
    private const int TipPriorityScanHorizon = 200;

    /// <summary>A ranked tip gets a reserved lane only when its commit sits in the first this-many
    /// rows (see <see cref="SeedTipPriority"/>); the trunk tip is pinned regardless of row. Tips
    /// deeper than this take a lane from the normal allocator when the walk reaches them —
    /// still deterministic (the walk is time-ordered), just not a column held empty from row 0.</summary>
    private const int PinLaneHorizon = 16;

    private static void SeedTipPriority(
        GraphLayoutState s,
        IReadOnlyList<RawCommit> commits,
        IReadOnlyDictionary<ObjectId, IReadOnlyList<GraphDecorationInput>>? decorations,
        Func<ObjectId, ObjectId, bool>? isAncestor)
    {
        if (decorations is null || decorations.Count == 0 || commits.Count < 2)
            return;

        var limit = Math.Min(commits.Count, TipPriorityScanHorizon);

        var hasChild = new HashSet<ObjectId>();
        for (var i = 0; i < limit; i++)
            foreach (var p in commits[i].ParentIds)
                hasChild.Add(p);

        var tips = new List<RawCommit>();
        var tipRow = new Dictionary<ObjectId, int>();
        for (var i = 0; i < limit; i++)
        {
            var c = commits[i];
            if (hasChild.Contains(c.Id))
                continue; // a ref pointing mid-history — it gets its lane by reservation, not here
            if (decorations.TryGetValue(c.Id, out var d)
                && d.Any(x => x.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch))
            {
                tips.Add(c);
                tipRow[c.Id] = i;
            }
        }
        if (tips.Count < 2)
            return;

        // §V10.1: newest tip first, by committer date; the SHA breaks an exact timestamp tie so
        // the order is stable per run.
        static int ByNewest(RawCommit a, RawCommit b)
        {
            var t = b.Metadata.CommitWhen.CompareTo(a.Metadata.CommitWhen);
            return t != 0 ? t : string.CompareOrdinal(a.Id.Sha, b.Id.Sha);
        }
        tips.Sort(ByNewest);

        // §V10.1 checked-out-branch exception. Locate the checked-out local branch's tip anywhere
        // in the scan window — it need NOT be a leaf: the whole point is that newer work has been
        // built on it. A detached HEAD carries no such badge, so nothing fires.
        RawCommit? head = null;
        for (var i = 0; i < limit; i++)
            if (decorations.TryGetValue(commits[i].Id, out var hd)
                && hd.Any(x => x.IsHead && x.Type == DecorationType.LocalBranch))
            {
                head = commits[i];
                break;
            }

        if (isAncestor is not null && head is { } h && !tips[0].Id.Equals(h.Id))
        {
            // The tips strictly newer than HEAD are the front run of the sorted list.
            var newerCount = 0;
            while (newerCount < tips.Count && ByNewest(tips[newerCount], h) < 0)
                newerCount++;

            // Newest-first, the first newer tip HEAD is an ancestor of.
            var promote = -1;
            for (var i = 0; i < newerCount; i++)
                if (!tips[i].Id.Equals(h.Id) && isAncestor(h.Id, tips[i].Id))
                {
                    promote = i;
                    break;
                }

            if (promote > 0)
            {
                // That tip takes the trunk; the tips ahead of it shift one lane right.
                var t = tips[promote];
                tips.RemoveAt(promote);
                tips.Insert(0, t);
            }
            else if (promote < 0)
            {
                // HEAD is an ancestor of no newer tip → HEAD itself takes the trunk. Only a leaf
                // HEAD reaches here (a non-leaf HEAD always has a newer descendant tip it is an
                // ancestor of), so it is already one of the candidates — just move it to front.
                var hi = tips.FindIndex(x => x.Id.Equals(h.Id));
                if (hi > 0)
                {
                    var t = tips[hi];
                    tips.RemoveAt(hi);
                    tips.Insert(0, t);
                }
            }
            // promote == 0 → HEAD is an ancestor of the current newest tip: order is unchanged.
        }

        // Pin the trunk tip (rank 0) to lane 0 always; pin the other ranked tips only when their
        // commit is near HEAD (row < PinLaneHorizon). Far-down tips fall back to the normal
        // allocator when the walk reaches them, so the near-HEAD region stays only as wide as the
        // branches truly live there. Compacted lanes are handed out by a running counter so the
        // pinned set stays contiguous 0..lane-1; the compaction map is pre-seeded to match so the
        // trunk renders in the leftmost column even when the walk emits a different tip on row 0.
        var cap = Math.Max(0, s.MaxLanes - 1);
        var lane = 0;
        for (var rank = 0; rank < tips.Count && lane < cap; rank++)
        {
            if (rank != 0 && tipRow.GetValueOrDefault(tips[rank].Id, int.MaxValue) >= PinLaneHorizon)
                continue;
            s.TipTargetLane[tips[rank].Id] = lane;
            s.CompactionMap[lane] = lane;
            lane++;
        }
        s.NextCompactedLane = Math.Max(s.NextCompactedLane, lane);
        s.LaneCeiling = Math.Max(s.LaneCeiling, lane);
    }

    private static int AllocLane(GraphLayoutState s)
    {
        if (s.FreeLanes.Count > 0)
        {
            var min = s.FreeLanes.Min;
            s.FreeLanes.Remove(min);
            return min;
        }

        if (s.LaneCeiling < s.MaxLanes - 1)
            return s.LaneCeiling++;

        return s.MaxLanes - 1; // overflow lane (rendered with a distinct dashed style)
    }

    private static void ReleaseLane(GraphLayoutState s, int rawLane)
    {
        if (rawLane >= s.MaxLanes - 1)
            return; // never free the overflow lane

        // Do not free a lane still expected by a pending parent reservation.
        if (s.ReservedLaneCount.GetValueOrDefault(rawLane) > 0)
            return;

        if (rawLane < s.LaneCeiling)
            s.FreeLanes.Add(rawLane);
    }

    private static int Compact(GraphLayoutState s, int rawLane)
    {
        if (s.CompactionMap.TryGetValue(rawLane, out var c))
            return c;
        c = s.NextCompactedLane++;
        s.CompactionMap[rawLane] = c;
        return c;
    }

    private static void AddReservation(GraphLayoutState s, ObjectId parent, Res res)
    {
        if (!s.Reservations.TryGetValue(parent, out var list))
            s.Reservations[parent] = list = [];
        list.Add(res);
        if (res.RawLane >= 0)
            s.ReservedLaneCount[res.RawLane] = s.ReservedLaneCount.GetValueOrDefault(res.RawLane) + 1;
    }

    private static void RemoveReservationsFor(GraphLayoutState s, ObjectId id, List<Res> removed)
    {
        foreach (var res in removed)
            if (res.RawLane >= 0)
            {
                var n = s.ReservedLaneCount.GetValueOrDefault(res.RawLane) - 1;
                if (n > 0)
                    s.ReservedLaneCount[res.RawLane] = n;
                else
                    s.ReservedLaneCount.Remove(res.RawLane);
            }
        s.Reservations.Remove(id);
    }

    // ── Color assignment (§7.5) ──

    private static int ResolveColor(GraphLayoutState s, int compactedLane,
        IReadOnlyList<GraphDecorationInput>? deco, ObjectId birthCommit)
    {
        if (s.LaneColor.TryGetValue(compactedLane, out var existing))
            return existing;

        // 1. Seed from a STABLE per-lane key.
        // §7.1 Correction (v8 M7, supersedes the "v4.5 review C" note): the key is the lane's
        // branch name when it has one — the lexicographically smallest, so several refs on the
        // birth commit cannot flip the choice between runs — otherwise the SHA of the commit
        // that first opened the lane. It never depends on the compacted-lane ordinal or on how
        // far the walk has progressed, so an incremental Extend and a full recompute seed every
        // lane identically and a repo reopens in the same shades.
        var branch = deco?
            .Where(x => x.Type is DecorationType.LocalBranch or DecorationType.RemoteBranch && x.Label.Length > 0)
            .Select(x => x.Label)
            .OrderBy(l => l, StringComparer.Ordinal)
            .FirstOrDefault();
        var laneKey = branch ?? birthCommit.Sha;
        s.LaneKey[compactedLane] = laneKey;

        var seed = (int)(StableHash.Fnv1a(laneKey) % LanePalette.Size);

        // 2. Deterministic ±2 de-collision: step +1 (mod palette) past the colours of nearby
        // lanes already coloured. Lane birth order is a pure function of the (stable) walk, and
        // a lane born later that lands on the same colour steps when it is itself coloured — so
        // the outcome does not depend on which side of a pair is processed first.
        var taken = new HashSet<int>();
        for (var l = compactedLane - 2; l <= compactedLane + 2; l++)
            if (l != compactedLane && s.LaneColor.TryGetValue(l, out var nearColor))
                taken.Add(nearColor);

        if (taken.Contains(seed))
        {
            for (var step = 1; step <= LanePalette.Size; step++)
            {
                var candidate = (seed + step) % LanePalette.Size;
                if (!taken.Contains(candidate))
                {
                    seed = candidate;
                    break;
                }
            }
        }

        s.LaneColor[compactedLane] = seed;
        return seed;
    }
}
