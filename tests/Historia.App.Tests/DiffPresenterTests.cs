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
        public bool CustomThemeActive => false;
        public Task SetCustomThemeEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task SelectCustomThemeAsync(string themeId, CancellationToken ct = default) => Task.CompletedTask;
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

    private static FileDiff CreateDiffEndingInAddedBlankLine()
    {
        var hunk = new DiffHunk
        {
            OldStart = 1,
            OldLineCount = 1,
            NewStart = 1,
            NewLineCount = 2,
            Header = "@@ -1,1 +1,2 @@",
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
                    Type = DiffLineType.Addition,
                    Content = "",
                    OldLineNumber = null,
                    NewLineNumber = 2,
                },
            ],
        };

        return new FileDiff
        {
            Path = "blank_end.txt",
            Kind = DiffKind.Modified,
            IsBinary = false,
            IsTruncated = false,
            Hunks = [hunk],
            AddedLines = 1,
            DeletedLines = 0,
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
    public void In_side_by_side_selecting_a_replaced_line_row_on_the_right_yields_addition_ref_only()
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
        Assert.Single(refs);
        Assert.Equal(new DiffLineRef(0, 2), refs[0]);
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

    [AvaloniaFact]
    public void Background_renderer_uses_palette_add_brush_after_show()
    {
        var theme = new StubThemeService { Effective = EffectiveTheme.Dark };
        using var applier = new AvaloniaThemeApplier(theme);

        var diff = CreateSampleDiff();
        var model = DiffDisplayModel.Create(diff);

        var presenter = new CombinedDiffPresenter { Model = model };
        var window = new Window { Width = 800, Height = 600, Content = presenter };
        window.Show();

        try
        {
            var brush = PaletteBrushes.Get("ColorDiffAddBg");
            Assert.NotNull(brush);
            Assert.NotEqual(Brushes.Transparent, brush);

            var expected = HistoriaPalette.Get(EffectiveTheme.Dark, "diff-add-bg").ToAvalonia();
            Assert.Equal(expected, ((ISolidColorBrush)brush).Color);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void User_scroll_on_left_pane_scrolls_right_pane()
    {
        var lines = Enumerable.Range(1, 100).Select(i => new DiffLine
        {
            Type = DiffLineType.Context,
            Content = $"line {i}",
            OldLineNumber = i,
            NewLineNumber = i,
        }).ToList();
        var hunk = new DiffHunk
        {
            OldStart = 1,
            OldLineCount = 100,
            NewStart = 1,
            NewLineCount = 100,
            Header = "@@ -1,100 +1,100 @@",
            Lines = lines,
        };
        var diff = new FileDiff
        {
            Path = "many.txt",
            Kind = DiffKind.Modified,
            IsBinary = false,
            IsTruncated = false,
            AddedLines = 0,
            DeletedLines = 0,
            Hunks = [hunk],
        };
        var model = DiffDisplayModel.Create(diff);

        var sbs = new SideBySideDiffPresenter();
        sbs.Model = model;

        var window = new Window { Width = 800, Height = 600, Content = sbs };
        window.Show();

        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var leftSv = sbs.LeftPresenter.FindDescendantOfType<ScrollViewer>();
            Assert.NotNull(leftSv);

            leftSv.Offset = new Vector(0, 50);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new Vector(0, 50), sbs.RightPresenter.GetScrollOffset());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Theme_change_between_same_variant_themes_redraws_washes()
    {
        var theme = new StubThemeService { Effective = EffectiveTheme.Dark };
        using var applier = new AvaloniaThemeApplier(theme);

        var diff = CreateSampleDiff();
        var model = DiffDisplayModel.Create(diff);

        var presenter = new CombinedDiffPresenter { Model = model };
        var window = new Window { Width = 800, Height = 600, Content = presenter };
        window.Show();

        try
        {
            var darkBg = ((ISolidColorBrush)PaletteBrushes.Get("ColorDiffAddBg")).Color;

            // Switch to HighContrast (same Avalonia ThemeVariant.Dark variant!)
            theme.Effective = EffectiveTheme.HighContrast;
            applier.Apply(EffectiveTheme.HighContrast);

            var hcBg = ((ISolidColorBrush)PaletteBrushes.Get("ColorDiffAddBg")).Color;
            var expectedHc = HistoriaPalette.Get(EffectiveTheme.HighContrast, "diff-add-bg").ToAvalonia();

            Assert.Equal(expectedHc, hcBg);
            Assert.NotEqual(darkBg, hcBg);
        }
        finally
        {
            theme.Effective = EffectiveTheme.Dark;
            applier.Apply(EffectiveTheme.Dark);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Sbs_row_button_only_in_left_pane()
    {
        var diff = CreateSampleDiff();
        var model = DiffDisplayModel.Create(diff);

        var sbs = new SideBySideDiffPresenter
        {
            Model = model,
            AllowStaging = true,
            StageVerb = "Stage",
        };

        var window = new Window { Width = 800, Height = 600, Content = sbs };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(sbs.LeftPresenter.TextArea.TextView.ElementGenerators, g => g is DiffHunkActionGenerator);
            Assert.DoesNotContain(sbs.RightPresenter.TextArea.TextView.ElementGenerators, g => g is DiffHunkActionGenerator);

            var leftButtons = sbs.LeftPresenter.GetVisualDescendants()
                .OfType<Button>()
                .Where(b => b.Content?.ToString() == "Stage hunk");
            Assert.NotEmpty(leftButtons);

            var rightButtons = sbs.RightPresenter.GetVisualDescendants()
                .OfType<Button>()
                .Where(b => b.Content?.ToString() == "Stage hunk");
            Assert.Empty(rightButtons);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Minimap_range_tracks_viewport()
    {
        var lines = Enumerable.Range(1, 100).Select(i => new DiffLine
        {
            Type = DiffLineType.Context,
            Content = $"line {i}",
            OldLineNumber = i,
            NewLineNumber = i,
        }).ToList();
        var hunk = new DiffHunk
        {
            OldStart = 1,
            OldLineCount = 100,
            NewStart = 1,
            NewLineCount = 100,
            Header = "@@ -1,100 +1,100 @@",
            Lines = lines,
        };
        var diff = new FileDiff
        {
            Path = "many.txt",
            Kind = DiffKind.Modified,
            IsBinary = false,
            IsTruncated = false,
            AddedLines = 0,
            DeletedLines = 0,
            Hunks = [hunk],
        };
        var model = DiffDisplayModel.Create(diff);

        var presenter = new CombinedDiffPresenter { Model = model };
        var minimap = new DiffMinimap { Model = model, Width = 12 };
        presenter.VisibleLineRangeChanged += (_, range) => minimap.DisplayRange = range;

        var panel = new DockPanel();
        panel.Children.Add(minimap);
        panel.Children.Add(presenter);

        var window = new Window { Width = 800, Height = 400, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(minimap.DisplayRange);
            var initialRange = minimap.DisplayRange.Value;
            Assert.True(initialRange.Start >= 1);
            Assert.True(initialRange.End > initialRange.Start);

            // Scroll down
            presenter.SetScrollOffset(new Vector(0, 300));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(minimap.DisplayRange);
            var scrolledRange = minimap.DisplayRange.Value;
            Assert.True(scrolledRange.Start > initialRange.Start);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Selection_change_keeps_the_same_context_menu_instance()
    {
        var diff = CreateSampleDiff();
        var model = DiffDisplayModel.Create(diff);
        var presenter = new CombinedDiffPresenter { Model = model, AllowStaging = true };

        var initialMenu = presenter.TextArea.ContextMenu;
        Assert.NotNull(initialMenu);

        // Select text
        presenter.TextArea.Selection = AvaloniaEdit.Editing.Selection.Create(presenter.TextArea, 0, 5);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(initialMenu, presenter.TextArea.ContextMenu);
    }

    [AvaloniaFact]
    public void Context_menu_reflects_selection_when_opened()
    {
        var diff = CreateSampleDiff();
        var model = DiffDisplayModel.Create(diff);
        var presenter = new CombinedDiffPresenter { Model = model, AllowStaging = true };

        var window = new Window { Width = 800, Height = 400, Content = presenter };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            var menu = presenter.TextArea.ContextMenu;
            Assert.NotNull(menu);

            // Initially no selection
            var stageLinesItem = menu.Items.OfType<MenuItem>().First(i => ((string)i.Header!).Contains("selected lines"));
            Assert.False(stageLinesItem.IsEnabled);

            // Select changed row (row 3 is deletion, 1-based line 3)
            var line3 = presenter.Document.GetLineByNumber(3);
            presenter.TextArea.Selection = AvaloniaEdit.Editing.Selection.Create(presenter.TextArea, line3.Offset, line3.EndOffset);
            Dispatcher.UIThread.RunJobs();

            // Raise Opening
            RaiseOpening(menu);

            stageLinesItem = menu.Items.OfType<MenuItem>().First(i => ((string)i.Header!).Contains("selected lines"));
            Assert.True(stageLinesItem.IsEnabled);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void RaiseOpening(ContextMenu menu)
    {
        var field = typeof(ContextMenu).GetField("Opening", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var handler = (System.ComponentModel.CancelEventHandler?)field?.GetValue(menu);
        handler?.Invoke(menu, new System.ComponentModel.CancelEventArgs());
    }

    [AvaloniaFact]
    public void Selection_ending_at_column_1_of_next_row_excludes_that_row()
    {
        var diff = CreateSampleDiff();
        var model = DiffDisplayModel.Create(diff);
        var presenter = new CombinedDiffPresenter { Model = model };

        // Line 3 is deletion (hunk 0, line 1), Line 4 is addition (hunk 0, line 2)
        var startOffset = presenter.Document.GetOffset(3, 1);
        var endOffset = presenter.Document.GetOffset(4, 1);
        presenter.TextArea.Selection = Selection.Create(presenter.TextArea, startOffset, endOffset);

        var refs = presenter.GetSelectedLineRefs();
        Assert.Single(refs);
        Assert.Equal(new DiffLineRef(0, 1), refs[0]);
    }

    [AvaloniaFact]
    public void Reverse_drag_ending_at_column_1_excludes_that_row()
    {
        var diff = CreateSampleDiff();
        var model = DiffDisplayModel.Create(diff);
        var presenter = new CombinedDiffPresenter { Model = model };

        // Anchor at line 4 col 1, caret at line 3 col 1
        var startOffset = presenter.Document.GetOffset(4, 1);
        var endOffset = presenter.Document.GetOffset(3, 1);
        presenter.TextArea.Selection = Selection.Create(presenter.TextArea, startOffset, endOffset);

        var refs = presenter.GetSelectedLineRefs();
        Assert.Single(refs);
        Assert.Equal(new DiffLineRef(0, 1), refs[0]);
    }

    [AvaloniaFact]
    public void Selection_ending_mid_row_includes_that_row()
    {
        var diff = CreateSampleDiff();
        var model = DiffDisplayModel.Create(diff);
        var presenter = new CombinedDiffPresenter { Model = model };

        var startOffset = presenter.Document.GetOffset(3, 1);
        var endOffset = presenter.Document.GetOffset(4, 2);
        presenter.TextArea.Selection = Selection.Create(presenter.TextArea, startOffset, endOffset);

        var refs = presenter.GetSelectedLineRefs();
        Assert.Equal(2, refs.Count);
        Assert.Equal(new DiffLineRef(0, 1), refs[0]);
        Assert.Equal(new DiffLineRef(0, 2), refs[1]);
    }

    [AvaloniaFact]
    public void Single_row_selection_is_unchanged()
    {
        var diff = CreateSampleDiff();
        var model = DiffDisplayModel.Create(diff);
        var presenter = new CombinedDiffPresenter { Model = model };

        var startOffset = presenter.Document.GetOffset(3, 1);
        var endOffset = presenter.Document.GetOffset(3, 3);
        presenter.TextArea.Selection = Selection.Create(presenter.TextArea, startOffset, endOffset);

        var refs = presenter.GetSelectedLineRefs();
        Assert.Single(refs);
        Assert.Equal(new DiffLineRef(0, 1), refs[0]);
    }

    [AvaloniaFact]
    public void Sbs_left_and_right_selection_apply_the_column_1_rule()
    {
        var diff = CreateSampleDiff();
        var model = DiffDisplayModel.Create(diff);
        var sbs = new SideBySideDiffPresenter { Model = model };

        // In side-by-side: Line 1 indicator, Line 2 context 1, Line 3 replacement (left=del, right=add), Line 4 context 2
        // 1. Right presenter: Line 3 is addition (hunk 0, line 2), Line 4 is context
        var rStart = sbs.RightPresenter.Document.GetOffset(3, 1);
        var rEnd = sbs.RightPresenter.Document.GetOffset(4, 1);
        sbs.RightPresenter.TextArea.Selection = Selection.Create(sbs.RightPresenter.TextArea, rStart, rEnd);

        var rightRefs = sbs.GetSelectedLineRefs();
        Assert.Single(rightRefs);
        Assert.Equal(new DiffLineRef(0, 2), rightRefs[0]);

        // 2. Left presenter: Line 3 is deletion (hunk 0, line 1), Line 4 is context
        sbs.RightPresenter.TextArea.ClearSelection();
        var lStart = sbs.LeftPresenter.Document.GetOffset(3, 1);
        var lEnd = sbs.LeftPresenter.Document.GetOffset(4, 1);
        sbs.LeftPresenter.TextArea.Selection = Selection.Create(sbs.LeftPresenter.TextArea, lStart, lEnd);

        var leftRefs = sbs.GetSelectedLineRefs();
        Assert.Single(leftRefs);
        Assert.Equal(new DiffLineRef(0, 1), leftRefs[0]);
    }

    [AvaloniaFact]
    public void Select_all_over_diff_ending_in_added_blank_line_includes_it()
    {
        var diff = CreateDiffEndingInAddedBlankLine();
        var model = DiffDisplayModel.Create(diff);
        var presenter = new CombinedDiffPresenter { Model = model };

        presenter.TextArea.Selection = Selection.Create(presenter.TextArea, 0, presenter.Document.TextLength);

        var refs = presenter.GetSelectedLineRefs();
        Assert.Single(refs);
        Assert.Equal(new DiffLineRef(0, 1), refs[0]);
    }

    [AvaloniaFact]
    public void Selection_from_previous_row_onto_empty_last_row_includes_it()
    {
        var diff = CreateDiffEndingInAddedBlankLine();
        var model = DiffDisplayModel.Create(diff);
        var presenter = new CombinedDiffPresenter { Model = model };

        var startOffset = presenter.Document.GetOffset(2, 1);
        var endOffset = presenter.Document.GetOffset(3, 1);
        presenter.TextArea.Selection = Selection.Create(presenter.TextArea, startOffset, endOffset);

        var refs = presenter.GetSelectedLineRefs();
        Assert.Single(refs);
        Assert.Equal(new DiffLineRef(0, 1), refs[0]);
    }

    [AvaloniaFact]
    public void Selection_ending_at_column_1_of_non_empty_last_row_still_excludes_it()
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
                new DiffLine { Type = DiffLineType.Context, Content = "c1", OldLineNumber = 1, NewLineNumber = 1 },
                new DiffLine { Type = DiffLineType.Addition, Content = "add1", OldLineNumber = null, NewLineNumber = 2 },
                new DiffLine { Type = DiffLineType.Addition, Content = "add2", OldLineNumber = null, NewLineNumber = 3 },
            ],
        };
        var diff = new FileDiff
        {
            Path = "non_empty_last.txt",
            Kind = DiffKind.Modified,
            IsBinary = false,
            IsTruncated = false,
            Hunks = [hunk],
            AddedLines = 2,
            DeletedLines = 0,
        };
        var model = DiffDisplayModel.Create(diff);
        var presenter = new CombinedDiffPresenter { Model = model };

        var startOffset = presenter.Document.GetOffset(3, 1);
        var endOffset = presenter.Document.GetOffset(4, 1);
        presenter.TextArea.Selection = Selection.Create(presenter.TextArea, startOffset, endOffset);

        var refs = presenter.GetSelectedLineRefs();
        Assert.Single(refs);
        Assert.Equal(new DiffLineRef(0, 1), refs[0]);
    }

    [AvaloniaFact]
    public void Select_all_in_sbs_right_pane_ending_in_blank_line_includes_it()
    {
        var diff = CreateDiffEndingInAddedBlankLine();
        var model = DiffDisplayModel.Create(diff);
        var sbs = new SideBySideDiffPresenter { Model = model };

        sbs.RightPresenter.TextArea.Selection = Selection.Create(
            sbs.RightPresenter.TextArea, 0, sbs.RightPresenter.Document.TextLength);

        var refs = sbs.GetSelectedLineRefs();
        Assert.Single(refs);
        Assert.Equal(new DiffLineRef(0, 1), refs[0]);
    }

    [AvaloniaFact]
    public void Selection_ending_at_column_1_of_empty_middle_row_still_excludes_it()
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
                new DiffLine { Type = DiffLineType.Addition, Content = "add1", OldLineNumber = null, NewLineNumber = 1 },
                new DiffLine { Type = DiffLineType.Addition, Content = "", OldLineNumber = null, NewLineNumber = 2 },
                new DiffLine { Type = DiffLineType.Addition, Content = "add3", OldLineNumber = null, NewLineNumber = 3 },
            ],
        };
        var diff = new FileDiff
        {
            Path = "middle_blank.txt",
            Kind = DiffKind.Modified,
            IsBinary = false,
            IsTruncated = false,
            Hunks = [hunk],
            AddedLines = 3,
            DeletedLines = 0,
        };
        var model = DiffDisplayModel.Create(diff);
        var presenter = new CombinedDiffPresenter { Model = model };

        var startOffset = presenter.Document.GetOffset(2, 1);
        var endOffset = presenter.Document.GetOffset(3, 1);
        presenter.TextArea.Selection = Selection.Create(presenter.TextArea, startOffset, endOffset);

        var refs = presenter.GetSelectedLineRefs();
        Assert.Single(refs);
        Assert.Equal(new DiffLineRef(0, 0), refs[0]);
    }
}
