namespace HistoriaApp.Tests;

/// <summary>
/// The diff minimap draws bucketed bars in their own visual and redraws them only when what
/// they show changes. Redrawing every row run on every scroll step produces no error, only
/// dropped frames on long diffs, so the bar count and the bars layer's render count are
/// asserted here.
/// </summary>
[Trait("tier", "ui")]
public sealed class DiffMinimapRenderTests
{
    private const double MinimapHeight = 400;

    /// <summary>A diff of <paramref name="groups"/> (deletion, addition, context) triples:
    /// two add/delete runs per group in the combined view.</summary>
    private static DiffDisplayModel AlternatingModel(int groups)
    {
        var lines = new List<DiffLine>(groups * 3);
        for (var g = 0; g < groups; g++)
        {
            lines.Add(new DiffLine { Type = DiffLineType.Deletion, Content = $"old {g}", OldLineNumber = 2 * g + 1, NewLineNumber = null });
            lines.Add(new DiffLine { Type = DiffLineType.Addition, Content = $"new {g}", OldLineNumber = null, NewLineNumber = 2 * g + 1 });
            lines.Add(new DiffLine { Type = DiffLineType.Context, Content = $"ctx {g}", OldLineNumber = 2 * g + 2, NewLineNumber = 2 * g + 2 });
        }

        var hunk = new DiffHunk
        {
            OldStart = 1,
            OldLineCount = groups * 2,
            NewStart = 1,
            NewLineCount = groups * 2,
            Header = $"@@ -1,{groups * 2} +1,{groups * 2} @@",
            Lines = lines,
        };
        return DiffDisplayModel.Create(new FileDiff
        {
            Path = "alternating.txt",
            Kind = DiffKind.Modified,
            IsBinary = false,
            IsTruncated = false,
            AddedLines = groups,
            DeletedLines = groups,
            Hunks = [hunk],
        });
    }

    private static (Window Window, DiffMinimap Minimap) Show(DiffDisplayModel model)
    {
        var minimap = new DiffMinimap { Model = model, Width = 12, Height = MinimapHeight };
        var window = new Window { Width = 200, Height = MinimapHeight + 100, Content = minimap };
        window.Show();
        Pump();
        return (window, minimap);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Thousands_of_runs_draw_at_most_two_bars_per_bucket_row()
    {
        var model = AlternatingModel(3_000);
        var (window, minimap) = Show(model);
        try
        {
            Assert.True(minimap.BarsRenderCountForTesting >= 1, "the bars layer must have rendered");
            Assert.True(minimap.BarRectCountForTesting > 0);
            Assert.True(minimap.BarRectCountForTesting <= 2 * MinimapHeight,
                $"expected at most {2 * MinimapHeight} bars for 6,000 runs, drew {minimap.BarRectCountForTesting}");

            minimap.IsSideBySide = true;
            Pump();
            // Two halves, each at most one bar per bucket row per kind.
            Assert.True(minimap.BarRectCountForTesting <= 4 * MinimapHeight);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Display_range_changes_do_not_redraw_the_bars()
    {
        var (window, minimap) = Show(AlternatingModel(3_000));
        try
        {
            var before = minimap.BarsRenderCountForTesting;
            Assert.True(before >= 1);

            for (var i = 0; i < 50; i++)
            {
                minimap.DisplayRange = (i * 10 + 1, i * 10 + 40);
                Pump();
            }

            Assert.Equal(before, minimap.BarsRenderCountForTesting);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Model_layout_mode_and_palette_changes_redraw_the_bars_once()
    {
        var (window, minimap) = Show(AlternatingModel(3_000));
        try
        {
            var count = minimap.BarsRenderCountForTesting;

            minimap.Model = AlternatingModel(1_000);
            Pump();
            Assert.Equal(count + 1, minimap.BarsRenderCountForTesting);
            count = minimap.BarsRenderCountForTesting;

            minimap.IsSideBySide = true;
            Pump();
            Assert.Equal(count + 1, minimap.BarsRenderCountForTesting);
            count = minimap.BarsRenderCountForTesting;

            PaletteBrushes.Invalidate();
            Pump();
            Assert.Equal(count + 1, minimap.BarsRenderCountForTesting);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Height_change_rebuckets_the_bars()
    {
        var (window, minimap) = Show(AlternatingModel(3_000));
        try
        {
            Assert.Equal((int)MinimapHeight, minimap.BarsBucketHeightForTesting);

            minimap.Height = MinimapHeight / 2;
            Pump();

            Assert.Equal((int)(MinimapHeight / 2), minimap.BarsBucketHeightForTesting);
            Assert.True(minimap.BarRectCountForTesting <= MinimapHeight,
                $"expected at most {MinimapHeight} bars at half height, drew {minimap.BarRectCountForTesting}");
        }
        finally
        {
            window.Close();
        }
    }
}
