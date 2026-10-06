namespace HistoriaApp.Tests;

/// <summary>
/// The diff margins draw line numbers and <c>+</c>/<c>-</c> from shaped layouts kept across
/// frames. Re-shaping every visible number each frame produces no error, only slower scrolling,
/// so the margins' cache misses are asserted: none on a repeat frame, and about one per newly
/// revealed line when scrolling.
/// </summary>
[Trait("tier", "ui")]
public sealed class DiffMarginTextTests
{
    private const int LineCount = 40_000;

    private sealed class StubThemeService : IThemeService
    {
        public EffectiveTheme Effective { get; set; } = EffectiveTheme.Dark;
        public ThemeMode CurrentMode => ThemeMode.Dark;
        public bool HighContrastEnabled => false;
        public string EffectiveCssThemeName => "dark";
        public IReadOnlyDictionary<string, Rgb> Palette => HistoriaPalette.For(Effective);
        public IReadOnlyList<Rgb> LaneColors => [];

#pragma warning disable CS0067
        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
#pragma warning restore CS0067

        public Task ApplyModeAsync(ThemeMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public bool CustomThemeActive => false;
        public Task SetCustomThemeEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task SelectCustomThemeAsync(string themeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHighContrastAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A long diff: context lines with a deletion/addition pair every tenth line.</summary>
    private static DiffDisplayModel LongModel()
    {
        var lines = new List<DiffLine>(LineCount);
        int oldNo = 1, newNo = 1;
        for (var i = 0; i < LineCount; i++)
        {
            if (i % 10 == 5)
                lines.Add(new DiffLine { Type = DiffLineType.Deletion, Content = $"old {i}", OldLineNumber = oldNo++, NewLineNumber = null });
            else if (i % 10 == 6)
                lines.Add(new DiffLine { Type = DiffLineType.Addition, Content = $"new {i}", OldLineNumber = null, NewLineNumber = newNo++ });
            else
                lines.Add(new DiffLine { Type = DiffLineType.Context, Content = $"line {i}", OldLineNumber = oldNo++, NewLineNumber = newNo++ });
        }

        var hunk = new DiffHunk
        {
            OldStart = 1,
            OldLineCount = oldNo - 1,
            NewStart = 1,
            NewLineCount = newNo - 1,
            Header = $"@@ -1,{oldNo - 1} +1,{newNo - 1} @@",
            Lines = lines,
        };
        return DiffDisplayModel.Create(new FileDiff
        {
            Path = "long.txt",
            Kind = DiffKind.Modified,
            IsBinary = false,
            IsTruncated = false,
            AddedLines = LineCount / 10,
            DeletedLines = LineCount / 10,
            Hunks = [hunk],
        });
    }

    private sealed record Setup(Window Window, CombinedDiffPresenter Presenter,
        IReadOnlyList<DiffLineNumberMargin> Numbers, DiffLineModifyTypeMargin Modify) : IDisposable
    {
        public void Dispose() => Window.Close();
    }

    private static Setup Show()
    {
        var presenter = new CombinedDiffPresenter { Model = LongModel() };
        var window = new Window { Width = 900, Height = 700, Content = presenter };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var numbers = presenter.TextArea.LeftMargins.OfType<DiffLineNumberMargin>().ToList();
        var modify = presenter.TextArea.LeftMargins.OfType<DiffLineModifyTypeMargin>().Single();
        Assert.Equal(2, numbers.Count);
        return new Setup(window, presenter, numbers, modify);
    }

    private static void RenderMargin(Control margin)
    {
        var size = new PixelSize(Math.Max(1, (int)margin.Bounds.Width), Math.Max(1, (int)margin.Bounds.Height));
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
        using var dc = bitmap.CreateDrawingContext();
        margin.Render(dc);
    }

    private static void RenderAll(Setup s)
    {
        s.Presenter.TextArea.TextView.EnsureVisualLines();
        foreach (var m in s.Numbers) RenderMargin(m);
        RenderMargin(s.Modify);
    }

    private static void ResetAll(Setup s)
    {
        foreach (var m in s.Numbers) m.TextCacheForTesting.ResetCounters();
        s.Modify.TextCacheForTesting.ResetCounters();
    }

    private static void ScrollTo(Setup s, double y)
    {
        s.Presenter.SetScrollOffset(new Vector(0, y));
        s.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static int VisibleLines(Setup s) => s.Presenter.TextArea.TextView.VisualLines.Count;

    private static int RowIndexOf(Setup s, DiffDisplayLineType type)
        => s.Presenter.Rows.Select((r, i) => (r, i)).First(x => x.r.Type == type).i;

    [AvaloniaFact]
    public void Number_margin_tints_added_and_deleted_rows()
    {
        using var applier = new AvaloniaThemeApplier(new StubThemeService());
        using var s = Show();
        int added = RowIndexOf(s, DiffDisplayLineType.Addition);
        int deleted = RowIndexOf(s, DiffDisplayLineType.Deletion);
        int context = RowIndexOf(s, DiffDisplayLineType.Context);

        var addGutter = HistoriaPalette.Get(EffectiveTheme.Dark, "diff-add-gutter").ToAvalonia();
        var delGutter = HistoriaPalette.Get(EffectiveTheme.Dark, "diff-del-gutter").ToAvalonia();
        foreach (var m in s.Numbers)
        {
            Assert.Equal(addGutter, Assert.IsAssignableFrom<ISolidColorBrush>(m.TintForRowForTesting(added)).Color);
            Assert.Equal(delGutter, Assert.IsAssignableFrom<ISolidColorBrush>(m.TintForRowForTesting(deleted)).Color);
            Assert.Null(m.TintForRowForTesting(context));
        }
    }

    [AvaloniaFact]
    public void Modify_type_margin_tints_rows()
    {
        using var applier = new AvaloniaThemeApplier(new StubThemeService());
        using var s = Show();
        var addGutter = HistoriaPalette.Get(EffectiveTheme.Dark, "diff-add-gutter").ToAvalonia();
        var delGutter = HistoriaPalette.Get(EffectiveTheme.Dark, "diff-del-gutter").ToAvalonia();
        Assert.Equal(addGutter, Assert.IsAssignableFrom<ISolidColorBrush>(s.Modify.TintForRowForTesting(RowIndexOf(s, DiffDisplayLineType.Addition))).Color);
        Assert.Equal(delGutter, Assert.IsAssignableFrom<ISolidColorBrush>(s.Modify.TintForRowForTesting(RowIndexOf(s, DiffDisplayLineType.Deletion))).Color);
        Assert.Null(s.Modify.TintForRowForTesting(RowIndexOf(s, DiffDisplayLineType.Context)));
    }

    [AvaloniaFact]
    public void Number_margin_width_includes_inner_padding()
    {
        using var s = Show();
        var p = s.Presenter;
        var numberWidth = new FormattedText(
            p.Model!.MaxLineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(p.FontFamily),
            p.FontSize,
            Brushes.White).Width;

        foreach (var m in s.Numbers)
        {
            Assert.Equal(new Thickness(0), m.Margin);
            Assert.Equal(numberWidth + 16, m.DesiredSize.Width, 0.5);
        }
        Assert.Equal(new Thickness(0), s.Modify.Margin);
    }

    [AvaloniaFact]
    public void A_repeat_frame_shapes_nothing()
    {
        using var s = Show();
        Assert.True(VisibleLines(s) > 10, $"expected a full viewport, saw {VisibleLines(s)} lines");

        RenderAll(s);
        foreach (var m in s.Numbers) Assert.True(m.TextCacheForTesting.Misses > 0, "the first frame must shape the visible numbers");
        Assert.Equal(2, s.Modify.TextCacheForTesting.Misses);

        ResetAll(s);
        RenderAll(s);

        foreach (var m in s.Numbers)
        {
            Assert.Equal(0, m.TextCacheForTesting.Misses);
            Assert.True(m.TextCacheForTesting.Hits > 0);
        }
        Assert.Equal(0, s.Modify.TextCacheForTesting.Misses);
    }

    [AvaloniaFact]
    public void Scrolling_n_lines_shapes_about_n_numbers()
    {
        using var s = Show();
        var lineHeight = s.Presenter.TextArea.TextView.DefaultLineHeight;
        // Start mid-document, away from anything shaped while the window first laid out.
        const int start = 20_000;
        ScrollTo(s, start * lineHeight);
        RenderAll(s);

        // Counters reset before the scroll: the window may render the margins itself on the way.
        const int n = 12;
        ResetAll(s);
        ScrollTo(s, (start + n) * lineHeight);
        RenderAll(s);

        foreach (var m in s.Numbers)
        {
            Assert.InRange(m.TextCacheForTesting.Misses, 1, n + 2);
        }
        Assert.Equal(0, s.Modify.TextCacheForTesting.Misses);
    }

    [AvaloniaFact]
    public void Paging_through_a_long_diff_stays_within_capacity_and_misses_only_revealed_lines()
    {
        using var s = Show();
        var view = s.Presenter.TextArea.TextView;
        var lineHeight = view.DefaultLineHeight;
        var visible = VisibleLines(s);

        // The capacity must hold several viewports so steady scrolling only shapes new numbers.
        Assert.True(DiffLineNumberMargin.TextCacheCapacity >= 4 * visible,
            $"capacity {DiffLineNumberMargin.TextCacheCapacity} must cover several {visible}-line viewports");

        RenderAll(s);
        var pageLines = Math.Max(1, visible - 2);
        var maxMissesPerPage = 0;
        var pages = 0;
        for (var top = 0; top + visible < LineCount && pages < 400; top += pageLines, pages++)
        {
            ResetAll(s);
            ScrollTo(s, top * lineHeight);
            RenderAll(s);
            foreach (var m in s.Numbers)
            {
                Assert.True(m.TextCacheForTesting.Hits + m.TextCacheForTesting.Misses > 0, $"page at line {top} drew nothing");
                maxMissesPerPage = Math.Max(maxMissesPerPage, m.TextCacheForTesting.Misses);
                Assert.True(m.TextCacheForTesting.Count <= DiffLineNumberMargin.TextCacheCapacity);
            }
        }

        Assert.True(maxMissesPerPage <= pageLines + 3,
            $"a {pageLines}-line page step shaped up to {maxMissesPerPage} numbers ({visible} visible)");
    }

    [AvaloniaFact]
    public void A_palette_change_clears_the_caches_and_redraws_with_the_new_brush()
    {
        var theme = new StubThemeService { Effective = EffectiveTheme.Dark };
        using var applier = new AvaloniaThemeApplier(theme);
        using var s = Show();
        RenderAll(s);
        var darkBrush = PaletteBrushes.Get("ColorTextFaint");
        foreach (var m in s.Numbers) Assert.True(m.TextCacheForTesting.Count > 0);

        theme.Effective = EffectiveTheme.Light;
        applier.Apply(EffectiveTheme.Light);

        foreach (var m in s.Numbers) Assert.Equal(0, m.TextCacheForTesting.Count);
        Assert.Equal(0, s.Modify.TextCacheForTesting.Count);

        var lightBrush = PaletteBrushes.Get("ColorTextFaint");
        Assert.NotSame(darkBrush, lightBrush);

        RenderAll(s);
        var p = s.Presenter;
        var face = new Typeface(p.FontFamily, p.FontStyle, p.FontWeight);
        var shown = p.TextArea.TextView.VisualLines
            .Select(l => p.Rows[l.FirstDocumentLine.LineNumber - 1].NewLineText)
            .First(t => t.Length > 0);
        // The second number margin shows new-side numbers.
        var cache = s.Numbers[1].TextCacheForTesting;
        cache.ResetCounters();
        cache.Get(shown, face, p.FontSize, lightBrush);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(0, cache.Misses);
    }
}
