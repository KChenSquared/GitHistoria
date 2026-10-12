namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class CommitDetailPanelLayoutTests : IDisposable
{
    private readonly TestRepoFixture _fixture;
    private readonly ServiceProvider _sp;
    private readonly RepositoryContext _context;

    public CommitDetailPanelLayoutTests()
    {
        _fixture = new TestRepoFixture();
        _sp = CreateServices();
        var ws = _sp.GetRequiredService<IWorkspaceService>();
        _context = ws.OpenAsync(_fixture.Path).GetAwaiter().GetResult();
        _context.State.RefreshRefsAsync().GetAwaiter().GetResult();
        ws.Focus(_context);
    }

    public void Dispose()
    {
        try { _context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _sp.Dispose(); } catch { }
        try { _fixture.Dispose(); } catch { }
    }

    [AvaloniaFact]
    public async Task Message_section_shows_the_reflowed_message()
    {
        const string rawMessage = "feat: title line 1\ntitle line 2\n\nbody paragraph 1\n\n- bullet item";
        var inlinesText = await LoadMessageTextAsync(rawMessage, assertNoModeButtons: true);
        Assert.Equal("feat: title line 1 title line 2\n\nbody paragraph 1\n\n- bullet item", inlinesText);
    }

    [AvaloniaFact]
    public async Task Message_section_keeps_list_items_and_trailers_on_their_own_lines()
    {
        const string rawMessage =
            "fix: keep the graph responsive\n\n" +
            "The hard-wrapped explanation of the change runs past the column the\n" +
            "editor wrapped it at and continues here.\n\n" +
            "- first bullet\n" +
            "- second bullet that wraps\n  onto a continuation line\n\n" +
            "Co-Authored-By: Someone <someone@example.com>";

        var inlinesText = await LoadMessageTextAsync(rawMessage, assertNoModeButtons: false);

        Assert.Equal(CommitMessageReflow.Reflow(rawMessage), inlinesText);
        var lines = inlinesText.Split('\n');
        Assert.Contains("The hard-wrapped explanation of the change runs past the column the editor wrapped it at and continues here.", lines);
        Assert.Contains("- first bullet", lines);
        Assert.Contains("- second bullet that wraps onto a continuation line", lines);
        Assert.Contains("Co-Authored-By: Someone <someone@example.com>", lines);
    }

    private async Task<string> LoadMessageTextAsync(string rawMessage, bool assertNoModeButtons)
    {
        _fixture.CreateCommitWithRawMessage(rawMessage);
        await _context.State.RefreshStatusAsync();
        var headId = _fixture.HeadCommitId;

        var view = new CommitDetailPanelView(_sp, _context, headId);
        var window = new Window { Width = 600, Height = 600, Content = view };
        window.Show();
        try
        {
            await WaitForCommitLoadedAsync(view);

            var tb = Assert.IsType<SelectableTextBlock>(view.MessageHostForTesting.Content);
            if (assertNoModeButtons)
            {
                var allButtons = view.GetVisualDescendants().OfType<Button>().ToList();
                Assert.DoesNotContain(allButtons, b => b.Content is "Raw" or "Rendered");
            }
            return string.Concat(tb.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Author_row_is_author_16_and_hash_14()
    {
        var headId = _fixture.HeadCommitId;
        var view = new CommitDetailPanelView(_sp, _context, headId);
        var window = new Window { Width = 600, Height = 600, Content = view };
        window.Show();
        try
        {
            await WaitForCommitLoadedAsync(view);

            Assert.NotNull(view.AuthorTextForTesting);
            Assert.NotNull(view.HashTextForTesting);

            Assert.Equal(16, view.AuthorTextForTesting.FontSize);
            Assert.Equal(14, view.HashTextForTesting.FontSize);
            Assert.NotNull(view.AuthorTextForTesting.Foreground);
            if (Application.Current?.TryFindResource("ColorText", out var res) == true && res is IBrush expectedBrush)
            {
                Assert.Equal(expectedBrush, view.AuthorTextForTesting.Foreground);
            }
            if (Application.Current?.TryFindResource("ColorAccent", out var accentRes) == true && accentRes is IBrush expectedAccent)
            {
                Assert.Equal(expectedAccent, view.HashTextForTesting.Foreground);
            }

            var metaRow = Assert.IsType<Grid>(view.MetaHostForTesting.Content);
            var allTextBlocks = metaRow.GetVisualDescendants().OfType<TextBlock>().ToList();
            Assert.Equal(2, allTextBlocks.Count);
            Assert.Contains(view.AuthorTextForTesting, allTextBlocks);
            Assert.Contains(view.HashTextForTesting, allTextBlocks);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Date_is_12_date_only_left_in_parents_row()
    {
        var headId = _fixture.HeadCommitId;
        var view = new CommitDetailPanelView(_sp, _context, headId);
        var window = new Window { Width = 600, Height = 600, Content = view };
        window.Show();
        try
        {
            await WaitForCommitLoadedAsync(view);

            var dateText = view.DateTextForTesting;
            Assert.NotNull(dateText);
            Assert.Equal(12, dateText.FontSize);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", dateText.Text);

            if (Application.Current?.TryFindResource("ColorTextMuted", out var mutedRes) == true && mutedRes is IBrush expectedMuted)
            {
                Assert.Equal(expectedMuted, dateText.Foreground);
            }

            var parentsRow = Assert.IsType<Grid>(view.ParentsHostForTesting.Content);
            Assert.Equal(0, Grid.GetColumn(dateText));

            var tip = ToolTip.GetTip(dateText)?.ToString();
            Assert.NotNull(tip);
            Assert.Contains("Committed", tip);
            Assert.Contains("Authored", tip);
            Assert.Matches(@"\d{2}:\d{2}", tip);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Parents_list_is_right_aligned()
    {
        var headId = _fixture.HeadCommitId;
        var view = new CommitDetailPanelView(_sp, _context, headId);
        var window = new Window { Width = 600, Height = 600, Content = view };
        window.Show();
        try
        {
            await WaitForCommitLoadedAsync(view);
            Dispatcher.UIThread.RunJobs();

            var parentsRow = Assert.IsType<Grid>(view.ParentsHostForTesting.Content);
            var parentsList = view.ParentsListForTesting;
            Assert.NotNull(parentsList);
            Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, parentsList.HorizontalAlignment);

            var rowBounds = parentsRow.Bounds;
            var listOrigin = parentsList.TranslatePoint(new Point(0, 0), parentsRow);
            Assert.NotNull(listOrigin);

            var listRightEdge = listOrigin.Value.X + parentsList.Bounds.Width;
            Assert.InRange(Math.Abs(rowBounds.Width - listRightEdge), 0, 1.01);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Parents_line_is_12_gray_label_white_underlined_hashes()
    {
        var headId = _fixture.HeadCommitId;
        var view = new CommitDetailPanelView(_sp, _context, headId);
        var window = new Window { Width = 600, Height = 600, Content = view };
        window.Show();
        try
        {
            await WaitForCommitLoadedAsync(view);

            var parentsList = view.ParentsListForTesting;
            Assert.NotNull(parentsList);
            var label = Assert.IsType<TextBlock>(parentsList.Children[0]);
            Assert.Equal(12, label.FontSize);
            Assert.Equal("Parent(s):", label.Text);
            Assert.NotNull(label.Foreground);
            if (Application.Current?.TryFindResource("ColorTextMuted", out var mutedRes) == true && mutedRes is IBrush expectedMuted)
            {
                Assert.Equal(expectedMuted, label.Foreground);
            }

            var hash = Assert.IsType<TextBlock>(parentsList.Children[1]);
            Assert.Equal(12, hash.FontSize);
            Assert.Equal(TextDecorations.Underline, hash.TextDecorations);
            Assert.NotNull(hash.Foreground);
            if (Application.Current?.TryFindResource("ColorText", out var textRes) == true && textRes is IBrush expectedText)
            {
                Assert.Equal(expectedText, hash.Foreground);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Parents_line_shows_every_parent()
    {
        // 1. Root commit (0 parents)
        var rootId = _fixture.RootCommitId;
        var rootView = new CommitDetailPanelView(_sp, _context, rootId);
        var rootWindow = new Window { Width = 600, Height = 600, Content = rootView };
        rootWindow.Show();
        try
        {
            await WaitForCommitLoadedAsync(rootView);
            var rootParentsList = rootView.ParentsListForTesting;
            Assert.NotNull(rootParentsList);
            Assert.Equal(2, rootParentsList.Children.Count);
            var noneText = Assert.IsType<TextBlock>(rootParentsList.Children[1]);
            Assert.Equal("none", noneText.Text);
        }
        finally
        {
            rootWindow.Close();
        }

        // 2. Normal commit (1 parent)
        var normalId = _fixture.HeadCommitId;
        var normalView = new CommitDetailPanelView(_sp, _context, normalId);
        var normalWindow = new Window { Width = 600, Height = 600, Content = normalView };
        normalWindow.Show();
        try
        {
            await WaitForCommitLoadedAsync(normalView);
            var normalParentsList = normalView.ParentsListForTesting;
            Assert.NotNull(normalParentsList);
            Assert.Equal(2, normalParentsList.Children.Count);
            var parentHash = Assert.IsType<TextBlock>(normalParentsList.Children[1]);
            Assert.Equal(rootId.ToShortString(10), parentHash.Text);
        }
        finally
        {
            normalWindow.Close();
        }

        // 3. Merge commit (2 parents)
        var (mergeId, parent1Id, parent2Id) = _fixture.CreateMergeCommit();
        var mergeView = new CommitDetailPanelView(_sp, _context, mergeId);
        var mergeWindow = new Window { Width = 600, Height = 600, Content = mergeView };
        mergeWindow.Show();
        try
        {
            await WaitForCommitLoadedAsync(mergeView);
            var mergeParentsList = mergeView.ParentsListForTesting;
            Assert.NotNull(mergeParentsList);
            Assert.Equal(3, mergeParentsList.Children.Count);
            var hash1 = Assert.IsType<TextBlock>(mergeParentsList.Children[1]);
            var hash2 = Assert.IsType<TextBlock>(mergeParentsList.Children[2]);
            Assert.Equal(parent1Id.ToShortString(10), hash1.Text);
            Assert.Equal(parent2Id.ToShortString(10), hash2.Text);
        }
        finally
        {
            mergeWindow.Close();
        }
    }

    [AvaloniaFact]
    public async Task Clicking_a_parent_focuses_it()
    {
        var (mergeId, parent1Id, parent2Id) = _fixture.CreateMergeCommit();
        var view = new CommitDetailPanelView(_sp, _context, mergeId);
        var window = new Window { Width = 600, Height = 600, Content = view };
        window.Show();
        try
        {
            await WaitForCommitLoadedAsync(view);

            var parentsList = view.ParentsListForTesting;
            Assert.NotNull(parentsList);
            var hash2 = Assert.IsType<TextBlock>(parentsList.Children[2]);

            GitObjectId? revealed = null;
            _context.State.RevealCommitRequested += (_, e) => revealed = e.CommitId;

            // Right click does not navigate or reveal
            var ptr = new Pointer(1, PointerType.Mouse, true);
            var peRight = new PointerPressedEventArgs(hash2, ptr, hash2, new Point(5, 5), 0,
                new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed), KeyModifiers.None);
            hash2.RaiseEvent(peRight);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(revealed);
            Assert.NotEqual(parent2Id, _context.State.SelectedCommitId);

            // Left click selects and reveals parent 2
            var peLeft = new PointerPressedEventArgs(hash2, ptr, hash2, new Point(5, 5), 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None);
            hash2.RaiseEvent(peLeft);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(parent2Id, _context.State.SelectedCommitId);
            Assert.Equal(parent2Id, revealed);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Author_block_gaps_and_rule()
    {
        var headId = _fixture.HeadCommitId;
        var view = new CommitDetailPanelView(_sp, _context, headId);
        var window = new Window { Width = 600, Height = 600, Content = view };
        window.Show();
        try
        {
            await WaitForCommitLoadedAsync(view);
            Dispatcher.UIThread.RunJobs();

            const double expectedAuthorGap = SectionFrame.HeaderGap + 4; // 6
            Assert.Equal(6, expectedAuthorGap);

            var topSection = view.TopSectionForTesting;
            var author = view.AuthorTextForTesting;
            Assert.NotNull(author);

            var authorOrigin = author.TranslatePoint(new Point(0, 0), topSection);
            Assert.NotNull(authorOrigin);
            var sectionInnerTop = topSection.BorderThickness.Top;
            var gapAboveAuthor = authorOrigin.Value.Y - sectionInnerTop;
            Assert.InRange(Math.Abs(gapAboveAuthor - expectedAuthorGap), 0, 1.01);

            var rule = view.AuthorRuleForTesting;
            Assert.Equal(1, rule.Height);

            var parentsHost = view.ParentsHostForTesting;
            var parentsBottom = parentsHost.TranslatePoint(new Point(0, parentsHost.Bounds.Height), topSection);
            var ruleTop = rule.TranslatePoint(new Point(0, 0), topSection);
            var ruleBottom = rule.TranslatePoint(new Point(0, rule.Bounds.Height), topSection);
            Assert.NotNull(parentsBottom);
            Assert.NotNull(ruleTop);
            Assert.NotNull(ruleBottom);

            var gapAboveRule = ruleTop.Value.Y - parentsBottom.Value.Y;
            Assert.InRange(Math.Abs(gapAboveRule - expectedAuthorGap), 0, 1.01);

            var messageScroll = Assert.IsType<ScrollViewer>(view.MessageHostForTesting.Parent);
            var messageTop = messageScroll.TranslatePoint(new Point(0, 0), topSection);
            Assert.NotNull(messageTop);

            var gapBelowRule = messageTop.Value.Y - ruleBottom.Value.Y;
            Assert.InRange(Math.Abs(gapBelowRule - SectionFrame.DividerBottomGap), 0, 1.01);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Files_label_is_16_and_rows_are_14()
    {
        await UsePathViewAsync();
        var headId = _fixture.HeadCommitId;
        var view = new CommitDetailPanelView(_sp, _context, headId);
        var window = new Window { Width = 600, Height = 600, Content = view };
        window.Show();
        try
        {
            await WaitForCommitLoadedAsync(view);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(16, view.FilesLabelForTesting.FontSize);

            // Path view (default)
            var pathRowGrid = view.FilesHostForTesting.GetVisualDescendants().OfType<Grid>().First(g => g.Height == 22);
            Assert.Equal(22, pathRowGrid.Height);
            var pathTextBlock = pathRowGrid.Children.OfType<TextBlock>().First(tb => tb.Text == "f.txt");
            Assert.Equal(14, pathTextBlock.FontSize);

            // Switch to Tree view
            var treeBtn = view.GetVisualDescendants().OfType<Button>().First(b => b.Content is "Tree");
            treeBtn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            var treeRowGrid = view.FilesHostForTesting.GetVisualDescendants().OfType<Grid>().First(g => g.Height == 22);
            Assert.Equal(22, treeRowGrid.Height);
            var treeTextBlock = treeRowGrid.Children.OfType<TextBlock>().First(tb => tb.Text == "f.txt");
            Assert.Equal(14, treeTextBlock.FontSize);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Top_section_has_the_changes_border()
    {
        var headId = _fixture.HeadCommitId;
        var view = new CommitDetailPanelView(_sp, _context, headId);
        Assert.Equal(new Thickness(1, 1, 1, 0), view.TopSectionForTesting.BorderThickness);
    }

    [AvaloniaFact]
    public async Task Files_bar_aligns_with_staged_bar()
    {
        var headId = _fixture.HeadCommitId;
        var windowStaging = new Window { Width = 800, Height = 900 };
        var windowDetail = new Window { Width = 800, Height = 900 };
        var stagingView = new StagingPanelView(_sp, _context);
        var detailView = new CommitDetailPanelView(_sp, _context, headId);

        windowStaging.Content = stagingView;
        windowDetail.Content = detailView;
        windowStaging.Show();
        windowDetail.Show();
        try
        {
            await WaitForCommitLoadedAsync(detailView);
            Dispatcher.UIThread.RunJobs();

            var filesY = detailView.FilesSectionForTesting.TranslatePoint(new Point(0, 0), windowDetail)!.Value.Y;
            var stagedSection = windowStaging.GetVisualDescendants().OfType<Border>().First(b => Grid.GetRow(b) == 2);
            var stagedY = stagedSection.TranslatePoint(new Point(0, 0), windowStaging)!.Value.Y;

            Assert.Equal(filesY, stagedY, 1);

            // Drag detail pane's divider handle down by 40 px
            var handle = detailView.FilesDividerHandleForTesting;
            var handleCenter = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), windowDetail)!.Value;

            Avalonia.Headless.HeadlessWindowExtensions.MouseDown(windowDetail, handleCenter, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Avalonia.Headless.HeadlessWindowExtensions.MouseMove(windowDetail, new Point(handleCenter.X, handleCenter.Y + 40));
            Dispatcher.UIThread.RunJobs();

            Avalonia.Headless.HeadlessWindowExtensions.MouseUp(windowDetail, new Point(handleCenter.X, handleCenter.Y + 40), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            var newStoredHeight = SectionFrame.GetSplitHeight(_context.Id);
            Assert.True(newStoredHeight > SectionFrame.DefaultTopHeight);

            // Re-attach stagingView and verify it adopts the updated stored height
            windowStaging.Content = null;
            Dispatcher.UIThread.RunJobs();
            windowStaging.Content = stagingView;
            Dispatcher.UIThread.RunJobs();

            var splitArea = windowStaging.GetVisualDescendants().OfType<Grid>().First(g => g.RowDefinitions.Count == 3);
            Assert.Equal(newStoredHeight, splitArea.RowDefinitions[0].Height.Value);
        }
        finally
        {
            windowStaging.Close();
            windowDetail.Close();
        }
    }

    [AvaloniaFact]
    public async Task Files_list_scrolls_horizontally_when_a_path_is_wider_than_the_panel()
    {
        await UsePathViewAsync();
        var longName = new string('a', 120) + ".txt";
        var longPath = "d/" + longName;
        var headId = _fixture.CreateCommitWithFile(longPath);
        var view = new CommitDetailPanelView(_sp, _context, headId);
        var window = new Window { Width = 300, Height = 600, Content = view };
        window.Show();
        try
        {
            await WaitForCommitLoadedAsync(view);
            Dispatcher.UIThread.RunJobs();

            Assert.False(view.FilesHostForTesting.Content is ItemsControl { Tag: "Tree" });
            var scroller = view.FilesScrollViewerForTesting;
            Assert.Equal(ScrollBarVisibility.Auto, scroller.HorizontalScrollBarVisibility);
            Assert.True(scroller.Extent.Width > scroller.Viewport.Width);

            var pathTextBlock = view.FilesHostForTesting.GetVisualDescendants().OfType<TextBlock>().First(tb => tb.Text == longPath);
            pathTextBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(pathTextBlock.Bounds.Width >= pathTextBlock.DesiredSize.Width - pathTextBlock.Margin.Left - 0.5);

            // Switch to Tree view
            var treeBtn = view.GetVisualDescendants().OfType<Button>().First(b => b.Content is "Tree");
            treeBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.True(view.FilesHostForTesting.Content is ItemsControl { Tag: "Tree" });
            Assert.True(scroller.Extent.Width > scroller.Viewport.Width);
            var treeTextBlock = view.FilesHostForTesting.GetVisualDescendants().OfType<TextBlock>().First(tb => tb.Text == longName);
            treeTextBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(treeTextBlock.Bounds.Width >= treeTextBlock.DesiredSize.Width - treeTextBlock.Margin.Left - 0.5);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Files_list_has_no_horizontal_overflow_when_paths_fit()
    {
        var headId = _fixture.HeadCommitId;
        var view = new CommitDetailPanelView(_sp, _context, headId);
        var window = new Window { Width = 600, Height = 600, Content = view };
        window.Show();
        try
        {
            await WaitForCommitLoadedAsync(view);
            Dispatcher.UIThread.RunJobs();

            var scroller = view.FilesScrollViewerForTesting;
            Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 0.5);

            var rowBorder = view.FilesHostForTesting.GetVisualDescendants()
                .OfType<Border>()
                .First(b => b.Child is Grid g && g.Height == 22);
            Assert.InRange(Math.Abs(rowBorder.Bounds.Width - scroller.Viewport.Width), 0, 1.0);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Files_list_stays_virtualized_with_horizontal_scroll()
    {
        var headId = _fixture.CreateCommitWithManyFiles(300);
        var view = new CommitDetailPanelView(_sp, _context, headId);
        var window = new Window { Width = 600, Height = 600, Content = view };
        window.Show();
        try
        {
            await WaitForCommitLoadedAsync(view);
            Dispatcher.UIThread.RunJobs();

            var realizedGrids = view.FilesHostForTesting.GetVisualDescendants()
                .OfType<Grid>()
                .Where(g => g.Height == 22)
                .ToList();

            Assert.True(realizedGrids.Count > 0);
            Assert.True(realizedGrids.Count < 50);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Staging_lists_keep_horizontal_scroll_disabled()
    {
        var stagingView = new StagingPanelView(_sp, _context);
        var window = new Window { Width = 800, Height = 900, Content = stagingView };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            var changesScroll = stagingView.ChangesRowsForTesting.FindAncestorOfType<ScrollViewer>();
            var stagedScroll = stagingView.StagedRowsForTesting.FindAncestorOfType<ScrollViewer>();

            Assert.Equal(ScrollBarVisibility.Disabled, changesScroll!.HorizontalScrollBarVisibility);
            Assert.Equal(ScrollBarVisibility.Disabled, stagedScroll!.HorizontalScrollBarVisibility);
        }
        finally
        {
            stagingView.Dispose();
            window.Close();
        }
    }

    private Task UsePathViewAsync() =>
        _sp.GetRequiredService<ISettingsService>().UpdateAsync(s => s with { CommitFilesView = CommitFilesView.Path });

    private static async Task WaitForCommitLoadedAsync(CommitDetailPanelView view)
    {
        for (var i = 0; i < 100; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (view.MetaHostForTesting.Content is not null && view.MessageHostForTesting.Content is not null)
                return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Commit detail did not finish loading in time.");
    }

    private sealed class TestRepoFixture : IDisposable
    {
        public string Path { get; }
        public GitObjectId RootCommitId { get; }
        public GitObjectId HeadCommitId => GitObjectId.Parse(RunGitOutput(Path, "rev-parse", "HEAD"));

        public TestRepoFixture()
        {
            Path = TestScratch.NewDirectory("historia-detail-tests-");
            RunGit(Path, "init", "-b", "main");
            RunGit(Path, "config", "core.autocrlf", "false");
            RunGit(Path, "config", "user.name", "Test");
            RunGit(Path, "config", "user.email", "test@example.com");

            var file = System.IO.Path.Combine(Path, "f.txt");
            File.WriteAllText(file, "init\n");
            RunGit(Path, "add", ".");
            RunGit(Path, "commit", "-m", "initial commit", "--no-gpg-sign");
            RootCommitId = GitObjectId.Parse(RunGitOutput(Path, "rev-parse", "HEAD"));

            File.WriteAllText(file, "second\n");
            RunGit(Path, "add", ".");
            RunGit(Path, "commit", "-m", "second commit", "--no-gpg-sign");
        }

        public GitObjectId CreateCommitWithFile(string relativePath)
        {
            var fullPath = System.IO.Path.Combine(Path, relativePath);
            var dir = System.IO.Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(fullPath, "content\n");
            RunGit(Path, "add", ".");
            RunGit(Path, "commit", "-m", $"add {relativePath}", "--no-gpg-sign");
            return HeadCommitId;
        }

        public GitObjectId CreateCommitWithManyFiles(int count)
        {
            for (var i = 0; i < count; i++)
            {
                var fullPath = System.IO.Path.Combine(Path, $"file_{i:D4}.txt");
                File.WriteAllText(fullPath, $"{i}\n");
            }
            RunGit(Path, "add", ".");
            RunGit(Path, "commit", "-m", $"add {count} files", "--no-gpg-sign");
            return HeadCommitId;
        }

        public void CreateCommitWithRawMessage(string msg)
        {
            var file = System.IO.Path.Combine(Path, "f.txt");
            File.AppendAllText(file, "more\n");
            RunGit(Path, "add", ".");
            RunGit(Path, "commit", "-m", msg, "--no-gpg-sign");
        }

        public (GitObjectId MergeId, GitObjectId Parent1, GitObjectId Parent2) CreateMergeCommit()
        {
            RunGit(Path, "checkout", "-b", "side");
            var sideFile = System.IO.Path.Combine(Path, "side.txt");
            File.WriteAllText(sideFile, "side\n");
            RunGit(Path, "add", ".");
            RunGit(Path, "commit", "-m", "side commit", "--no-gpg-sign");
            var parent2 = GitObjectId.Parse(RunGitOutput(Path, "rev-parse", "HEAD"));

            RunGit(Path, "checkout", "main");
            var mainFile = System.IO.Path.Combine(Path, "main.txt");
            File.WriteAllText(mainFile, "main\n");
            RunGit(Path, "add", ".");
            RunGit(Path, "commit", "-m", "main commit", "--no-gpg-sign");
            var parent1 = GitObjectId.Parse(RunGitOutput(Path, "rev-parse", "HEAD"));

            RunGit(Path, "merge", "side", "--no-ff", "-m", "merge side into main", "--no-gpg-sign");
            var mergeId = GitObjectId.Parse(RunGitOutput(Path, "rev-parse", "HEAD"));
            return (mergeId, parent1, parent2);
        }

        private static void RunGit(string dir, params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GIT_AUTHOR_NAME"] = "Test";
            psi.Environment["GIT_AUTHOR_EMAIL"] = "test@example.com";
            psi.Environment["GIT_COMMITTER_NAME"] = "Test";
            psi.Environment["GIT_COMMITTER_EMAIL"] = "test@example.com";
            using var p = System.Diagnostics.Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            Task.WaitAll(outTask, errTask);
        }

        private static string RunGitOutput(string dir, params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var p = System.Diagnostics.Process.Start(psi)!;
            var errTask = p.StandardError.ReadToEndAsync();
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            errTask.Wait();
            return stdout.Trim();
        }

        public void Dispose() => TestScratch.Delete(Path);
    }

    private static ServiceProvider CreateServices()
    {
        var sc = new ServiceCollection();
        var settings = SettingsService.Load();
        sc.AddSingleton<ISettingsService>(settings);
        sc.AddSingleton<SettingsService>(settings);
        sc.AddSingleton<IUiDispatcher, Historia.Platform.UiDispatcher>();
        sc.AddSingleton<IDialogService, Historia.Platform.AvaloniaDialogService>();
        sc.AddSingleton<IScrollDirectionProbe, Historia.Platform.ScrollDirectionProbe>();
        sc.AddSingleton<IRepoActionBus, RepoActionBus>();
        sc.AddSingleton<IRepoNetworkActionRunner, RepoNetworkActionRunner>();
        sc.AddSingleton<IAutoFetchHost, AutoFetchHost>();
        sc.AddSingleton<IAutoFetchService, AutoFetchService>();
        sc.AddSingleton<INavigationBus, NavigationBus>();
        sc.AddSingleton<IKeyboardCommandRouter, KeyboardCommandRouter>();
        sc.AddSingleton<INotificationService, NotificationService>();
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton<ISystemThemeProbe, FakeThemeProbe>();
        sc.AddSingleton<IThemeService, ThemeService>();
        sc.AddSingleton<ICustomThemeService, CustomThemeService>();
        sc.AddSingleton<IWorkspaceService, WorkspaceService>();
        sc.AddSingleton<IProfileService, ProfileService>();
        sc.AddSingleton<IGitCliService, GitCliService>();
        var locator = new GitExecutableLocator();
        locator.ResolveAsync(null).GetAwaiter().GetResult();
        sc.AddSingleton<IGitExecutableLocator>(locator);
        sc.AddSingleton<IBackgroundWorkGate, BackgroundWorkGate>();
        sc.AddSingleton<IGraphLayoutService, GraphLayoutService>();
        sc.AddSingleton<IRepoStateCache, RepoStateCache>();
        sc.AddSingleton<IStashService, StashService>();
        sc.AddSingleton<IConflictService, ConflictService>();
        sc.AddSingleton<IPatchService, PatchService>();
        sc.AddSingleton<ICherryPickService, CherryPickService>();
        sc.AddSingleton<IRebaseService, RebaseService>();
        sc.AddSingleton<ISubmoduleService, SubmoduleService>();
        sc.AddSingleton<IWorktreeService, WorktreeService>();
        sc.AddSingleton<ISshKeyService, SshKeyService>();
        sc.AddSingleton<ICloneService, CloneService>();
        sc.AddSingleton<ITerminalBackend, PortaPtyBackend>();
        sc.AddSingleton<ITerminalSessionService, TerminalSessionService>();
        sc.AddSingleton<IRecentRepositoryService, RecentRepositoryService>();
        sc.AddSingleton<IRepoIdentityService, RepoIdentityService>();
        sc.AddSingleton<ICredentialVaultService, FakeVault>();
        sc.AddSingleton<IExternalLauncher, ExternalLauncher>();
        return sc.BuildServiceProvider();
    }

    private sealed class FakeThemeProbe : ISystemThemeProbe
    {
        public bool IsLightTheme => false;
        public bool IsHighContrast => false;
#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067
    }

    private sealed class FakeVault : ICredentialVaultService
    {
        public Task StoreTokenAsync(string key, string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> RetrieveTokenAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteTokenAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }
}
