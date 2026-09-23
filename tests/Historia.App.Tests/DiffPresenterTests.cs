namespace HistoriaApp.Tests;

public class DiffPresenterTests
{
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
        public Task ApplyCustomThemeAsync(string themeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHighContrastAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static FileDiff CreateSampleDiff(string delText = "old text", string addText = "new text")
    {
        var hunk = new DiffHunk
        {
            OldStart = 1,
            OldLineCount = 3,
            NewStart = 1,
            NewLineCount = 3,
            Header = "@@ -1,3 +1,3 @@",
            Lines =
            [
                new DiffLine
                {
                    Type = DiffLineType.Context,
                    Content = "context 1",
                    OldLineNumber = 1,
                    NewLineNumber = 1,
                },
                new DiffLine
                {
                    Type = DiffLineType.Deletion,
                    Content = delText,
                    OldLineNumber = 2,
                    NewLineNumber = null,
                },
                new DiffLine
                {
                    Type = DiffLineType.Addition,
                    Content = addText,
                    OldLineNumber = null,
                    NewLineNumber = 2,
                },
                new DiffLine
                {
                    Type = DiffLineType.Context,
                    Content = "context 2",
                    OldLineNumber = 3,
                    NewLineNumber = 3,
                },
            ],
        };

        return new FileDiff
        {
            Path = "sample.txt",
            Kind = DiffKind.Modified,
            IsBinary = false,
            IsTruncated = false,
            Hunks = [hunk],
            AddedLines = 1,
            DeletedLines = 1,
        };
    }

    [AvaloniaFact]
    public void Combined_presenter_document_line_count_equals_model()
    {
        var diff = CreateSampleDiff();
        var model = DiffDisplayModel.Create(diff);

        var presenter = new CombinedDiffPresenter();
        presenter.Model = model;

        Assert.Equal(model.CombinedRows.Count, presenter.Document.LineCount);
    }

    [AvaloniaFact]
    public void Side_by_side_scroll_mirroring_mirrors_offset_without_feedback_loop()
    {
        var diff = CreateSampleDiff();
        var model = DiffDisplayModel.Create(diff);

        var sbs = new SideBySideDiffPresenter();
        sbs.Model = model;

        // Set left offset -> right mirrors
        sbs.LeftPresenter.SetScrollOffset(new Vector(0, 100));
        Assert.Equal(new Vector(0, 100), sbs.RightPresenter.GetScrollOffset());

        // Set right offset -> left mirrors
        sbs.RightPresenter.SetScrollOffset(new Vector(0, 200));
        Assert.Equal(new Vector(0, 200), sbs.LeftPresenter.GetScrollOffset());
    }

    [AvaloniaFact]
    public void Hunk_action_event_carries_the_right_index()
    {
        var presenter = new CombinedDiffPresenter();
        int receivedCombined = -1;
        presenter.HunkActionRequested += (_, hi) => receivedCombined = hi;
        presenter.RequestHunkAction(3);
        Assert.Equal(3, receivedCombined);

        var sbs = new SideBySideDiffPresenter();
        int receivedSbs = -1;
        sbs.HunkActionRequested += (_, hi) => receivedSbs = hi;
        sbs.RequestHunkAction(5);
        Assert.Equal(5, receivedSbs);
    }

    [AvaloniaFact]
    public void Selection_spanning_context_and_two_additions_yields_exactly_those_two_refs()
    {
        var hunk = new DiffHunk
        {
            OldStart = 1,
            OldLineCount = 1,
            NewStart = 1,
            NewLineCount = 3,
            Header = "@@ -1,1 +1,3 @@",
            Lines =
            [
                new DiffLine
                {
                    Type = DiffLineType.Context,
                    Content = "context line",
                    OldLineNumber = 1,
                    NewLineNumber = 1,
                },
                new DiffLine
                {
                    Type = DiffLineType.Addition,
                    Content = "added 1",
                    OldLineNumber = null,
                    NewLineNumber = 2,
                },
                new DiffLine
                {
                    Type = DiffLineType.Addition,
                    Content = "added 2",
                    OldLineNumber = null,
                    NewLineNumber = 3,
                },
            ],
        };

        var diff = new FileDiff
        {
            Path = "add.txt",
            Kind = DiffKind.Modified,
            IsBinary = false,
            IsTruncated = false,
            Hunks = [hunk],
            AddedLines = 2,
            DeletedLines = 0,
        };

        var model = DiffDisplayModel.Create(diff);
        var presenter = new CombinedDiffPresenter();
        presenter.Model = model;

        // Line 1 is Indicator, Line 2 is Context, Line 3 is Addition 1, Line 4 is Addition 2
        // Select lines 2..4 (covering context and both additions)
        var startOffset = presenter.Document.GetOffset(2, 1);
        var endOffset = presenter.Document.GetOffset(4, 5);
        presenter.TextArea.Selection = Selection.Create(presenter.TextArea, startOffset, endOffset);

        var refs = presenter.GetSelectedLineRefs();
        Assert.Equal(2, refs.Count);
        Assert.Equal(new DiffLineRef(0, 1), refs[0]);
        Assert.Equal(new DiffLineRef(0, 2), refs[1]);
    }

    [AvaloniaFact]
    public void In_side_by_side_selecting_a_replaced_line_row_on_the_right_yields_both_plus_ref_and_paired_minus_ref()
    {
        var diff = CreateSampleDiff();
        var model = DiffDisplayModel.Create(diff);

        var sbs = new SideBySideDiffPresenter();
        sbs.Model = model;

        // Line 1 is Indicator, Line 2 is Context ("context 1"), Line 3 is Replacement:
        // Left has Deletion (hunk 0, line 1: "old text")
        // Right has Addition (hunk 0, line 2: "new text")
        var startOffset = sbs.RightPresenter.Document.GetOffset(3, 1);
        var endOffset = sbs.RightPresenter.Document.GetOffset(3, 3);
        sbs.RightPresenter.TextArea.Selection = Selection.Create(sbs.RightPresenter.TextArea, startOffset, endOffset);

        var refs = sbs.GetSelectedLineRefs();
        Assert.Equal(2, refs.Count);
        Assert.Contains(new DiffLineRef(0, 1), refs);
        Assert.Contains(new DiffLineRef(0, 2), refs);
    }

    [AvaloniaFact]
    public async Task Copying_a_row_holding_cr_symbol_puts_cr_on_the_clipboard()
    {
        var rawContent = "hello\rworld";
        var hunk = new DiffHunk
        {
            OldStart = 1,
            OldLineCount = 1,
            NewStart = 1,
            NewLineCount = 1,
            Header = "@@ -1,1 +1,1 @@",
            Lines =
            [
                new DiffLine
                {
                    Type = DiffLineType.Addition,
                    Content = rawContent,
                    OldLineNumber = null,
                    NewLineNumber = 1,
                },
            ],
        };

        var diff = new FileDiff
        {
            Path = "cr.txt",
            Kind = DiffKind.Modified,
            IsBinary = false,
            IsTruncated = false,
            Hunks = [hunk],
            AddedLines = 1,
            DeletedLines = 0,
        };

        var model = DiffDisplayModel.Create(diff);
        var presenter = new CombinedDiffPresenter();
        presenter.Model = model;

        var window = new Window { Content = presenter };
        window.Show();

        // Line 1 is indicator, Line 2 is addition holding "hello␍world"
        var startOffset = presenter.Document.GetOffset(2, 1);
        var endOffset = presenter.Document.GetOffset(2, 12);
        presenter.TextArea.Selection = Selection.Create(presenter.TextArea, startOffset, endOffset);

        await presenter.CopySelectionAsync();

#pragma warning disable CS0618
        var clipText = await window.Clipboard!.GetTextAsync();
#pragma warning restore CS0618
        Assert.NotNull(clipText);
        Assert.Contains('\r', clipText);
        Assert.DoesNotContain(DiffDisplayModel.CarriageReturnSymbol, clipText);

        window.Close();
    }

    [AvaloniaFact]
    public void SyntaxThemeHost_absent_does_not_throw()
    {
        var presenter = new CombinedDiffPresenter();
        presenter.ServicesProvider = null;

        var window = new Window { Content = presenter };
        window.Show();
        // Attaching without a host should stay plain text and not throw
        window.Close();
    }

    [AvaloniaFact]
    public void Showing_a_presenter_in_a_window_raises_SyntaxThemeHost_LiveCount_and_removing_brings_it_down()
    {
        var themeService = new StubThemeService();
        using var host = new SyntaxThemeHost(themeService);

        var services = new ServiceCollection();
        services.AddSingleton(host);
        var sp = services.BuildServiceProvider();

        // 1. Combined presenter
        var combined = new CombinedDiffPresenter
        {
            ServicesProvider = sp,
            FilePath = "test.cs",
        };

        Assert.Equal(0, host.LiveCount);

        var window1 = new Window { Content = combined };
        window1.Show();
        Assert.Equal(1, host.LiveCount);

        window1.Content = null;
        Assert.Equal(0, host.LiveCount);
        window1.Close();

        // 2. SideBySide presenter (both left and right editors attach)
        var sbs = new SideBySideDiffPresenter
        {
            ServicesProvider = sp,
            FilePath = "test.cs",
        };

        Assert.Equal(0, host.LiveCount);

        var window2 = new Window { Content = sbs };
        window2.Show();
        Assert.Equal(2, host.LiveCount);

        window2.Content = null;
        Assert.Equal(0, host.LiveCount);
        window2.Close();
    }
}
