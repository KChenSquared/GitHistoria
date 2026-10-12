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

            if (assertNoModeButtons)
            {
                var allButtons = view.GetVisualDescendants().OfType<Button>().ToList();
                Assert.DoesNotContain(allButtons, b => b.Content is "Raw" or "Rendered");
            }
            return string.Concat(view.MessageText.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
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

            Assert.Equal(16, view.AuthorText.FontSize);
            Assert.Equal(14, view.HashText.FontSize);
            Assert.NotNull(view.AuthorText.Foreground);
            if (Application.Current?.TryFindResource("ColorText", out var res) == true && res is IBrush expectedBrush)
            {
                Assert.Equal(expectedBrush, view.AuthorText.Foreground);
            }
            if (Application.Current?.TryFindResource("ColorAccent", out var accentRes) == true && accentRes is IBrush expectedAccent)
            {
                Assert.Equal(expectedAccent, view.HashText.Foreground);
            }

            Assert.Equal("Test", view.AuthorText.Text);
            Assert.Equal(view.ViewModel.ShortHash, view.HashText.Text);
            Assert.True(IsUnderlined(view.HashText));

            var allTextBlocks = view.MetaRow.GetVisualDescendants().OfType<TextBlock>().ToList();
            Assert.Equal(2, allTextBlocks.Count);
            Assert.Contains(view.AuthorText, allTextBlocks);
            Assert.Contains(view.HashText, allTextBlocks);
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

            var dateText = view.DateText;
            Assert.Equal(12, dateText.FontSize);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", dateText.Text);

            if (Application.Current?.TryFindResource("ColorTextMuted", out var mutedRes) == true && mutedRes is IBrush expectedMuted)
            {
                Assert.Equal(expectedMuted, dateText.Foreground);
            }

            Assert.Same(view.ParentsRow, dateText.GetVisualAncestors().OfType<Grid>().First());
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
    public async Task Date_follows_the_author_date_setting_live()
    {
        var settings = _sp.GetRequiredService<ISettingsService>();
        await settings.UpdateAsync(s => s with { ShowAuthorDate = false });
        var headId = _fixture.HeadCommitId;
        var view = new CommitDetailPanelView(_sp, _context, headId);
        var window = new Window { Width = 600, Height = 600, Content = view };
        window.Show();
        try
        {
            await WaitForCommitLoadedAsync(view);
            var detail = view.ViewModel.Detail!;
            Assert.Equal($"{detail.CommitWhen:yyyy-MM-dd}", view.DateText.Text);

            await settings.UpdateAsync(s => s with { ShowAuthorDate = true });
            Dispatcher.UIThread.RunJobs();

            Assert.Equal($"{detail.AuthorWhen:yyyy-MM-dd}", view.DateText.Text);
        }
        finally
        {
            await settings.UpdateAsync(s => s with { ShowAuthorDate = false });
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

            var parentsRow = view.ParentsRow;
            var parentsList = view.ParentsList;
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

            var label = view.ParentsList.GetVisualDescendants().OfType<TextBlock>().First();
            Assert.Equal(12, label.FontSize);
            Assert.Equal("Parent(s):", label.Text);
            Assert.NotNull(label.Foreground);
            if (Application.Current?.TryFindResource("ColorTextMuted", out var mutedRes) == true && mutedRes is IBrush expectedMuted)
            {
                Assert.Equal(expectedMuted, label.Foreground);
            }

            var hash = Assert.Single(ParentHashes(view));
            Assert.Equal(12, hash.FontSize);
            Assert.True(IsUnderlined(hash));
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
            Assert.Empty(ParentHashes(rootView));
            var noneText = rootView.ParentsList.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "none");
            Assert.True(noneText.IsEffectivelyVisible);
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
            var parentHash = Assert.Single(ParentHashes(normalView));
            Assert.Equal(rootId.ToShortString(10), parentHash.Text);
            Assert.DoesNotContain(normalView.ParentsList.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text == "none" && t.IsEffectivelyVisible);
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
            var hashes = ParentHashes(mergeView);
            Assert.Equal(2, hashes.Count);
            Assert.Equal(parent1Id.ToShortString(10), hashes[0].Text);
            Assert.Equal(parent2Id.ToShortString(10), hashes[1].Text);
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

            var hash2 = ParentHashes(view)[1];

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

            var topSection = view.TopSection;
            var author = view.AuthorText;

            var authorOrigin = author.TranslatePoint(new Point(0, 0), topSection);
            Assert.NotNull(authorOrigin);
            var sectionInnerTop = topSection.BorderThickness.Top;
            var gapAboveAuthor = authorOrigin.Value.Y - sectionInnerTop;
            Assert.InRange(Math.Abs(gapAboveAuthor - expectedAuthorGap), 0, 1.01);

            var rule = view.AuthorRule;
            Assert.Equal(1, rule.Height);

            var parentsHost = view.ParentsRow;
            var parentsBottom = parentsHost.TranslatePoint(new Point(0, parentsHost.Bounds.Height), topSection);
            var ruleTop = rule.TranslatePoint(new Point(0, 0), topSection);
            var ruleBottom = rule.TranslatePoint(new Point(0, rule.Bounds.Height), topSection);
            Assert.NotNull(parentsBottom);
            Assert.NotNull(ruleTop);
            Assert.NotNull(ruleBottom);

            var gapAboveRule = ruleTop.Value.Y - parentsBottom.Value.Y;
            Assert.InRange(Math.Abs(gapAboveRule - expectedAuthorGap), 0, 1.01);

            var messageTop = view.MessageScroll.TranslatePoint(new Point(0, 0), topSection);
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

            Assert.Equal(16, view.FilesLabel.FontSize);

            // Path view (default)
            var pathRowGrid = view.FilesList.GetVisualDescendants().OfType<Grid>().First(g => g.Height == 22);
            Assert.Equal(22, pathRowGrid.Height);
            var pathTextBlock = pathRowGrid.Children.OfType<TextBlock>().First(tb => tb.Text == "f.txt");
            Assert.Equal(14, pathTextBlock.FontSize);

            // Switch to Tree view
            Click(window, ModeButton(view, "Tree"));

            var treeRowGrid = view.FilesList.GetVisualDescendants().OfType<Grid>().First(g => g.Height == 22);
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
    public async Task Files_mode_buttons_switch_the_mode_and_mark_the_active_one()
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
            var pathButton = ModeButton(view, "Path");
            var treeButton = ModeButton(view, "Tree");
            Assert.Equal(FontWeight.SemiBold, pathButton.FontWeight);
            Assert.Equal(FontWeight.Normal, treeButton.FontWeight);

            Click(window, treeButton);

            Assert.Equal(CommitFilesView.Tree, _sp.GetRequiredService<ISettingsService>().Current.CommitFilesView);
            Assert.True(view.ViewModel.IsTreeMode);
            Assert.Equal(FontWeight.Normal, pathButton.FontWeight);
            Assert.Equal(FontWeight.SemiBold, treeButton.FontWeight);

            Click(window, pathButton);

            Assert.Equal(CommitFilesView.Path, _sp.GetRequiredService<ISettingsService>().Current.CommitFilesView);
            Assert.Equal(FontWeight.SemiBold, pathButton.FontWeight);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Clicking_a_folder_row_collapses_it_and_clicking_a_file_row_opens_its_diff()
    {
        await _sp.GetRequiredService<ISettingsService>().UpdateAsync(s => s with { CommitFilesView = CommitFilesView.Tree });
        try
        {
            var headId = _fixture.CreateCommitWithFile("d/x/y.txt");
            var view = new CommitDetailPanelView(_sp, _context, headId);
            var window = new Window { Width = 600, Height = 600, Content = view };
            window.Show();
            try
            {
                await WaitForCommitLoadedAsync(view);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(["d/", "x/", "y.txt"], RowTexts(view));

                Click(window, RowBorders(view).First(b => b.DataContext is CommitFolderRowViewModel { Name: "x" }));

                Assert.Equal(["d/", "x/"], RowTexts(view));

                Click(window, RowBorders(view).First(b => b.DataContext is CommitFolderRowViewModel { Name: "x" }));
                Assert.Equal(["d/", "x/", "y.txt"], RowTexts(view));

                Click(window, RowBorders(view).First(b => b.DataContext is CommitFileRowViewModel));

                var diff = Assert.IsType<DiffRequest>(_context.State.OpenDiff);
                Assert.Equal("d/x/y.txt", diff.Path);
                Assert.Equal(headId, diff.CommitId);
                Assert.Equal(DiffSource.Commit, diff.Source);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            await UsePathViewAsync();
        }
    }

    [AvaloniaFact]
    public async Task A_file_row_offers_reveal_and_copy_path_in_its_context_menu()
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

            var row = RowBorders(view).First(b => b.DataContext is CommitFileRowViewModel);
            var menu = Assert.IsType<ContextMenu>(row.ContextMenu);
            var headers = menu.Items.OfType<MenuItem>().Select(m => m.Header).ToList();
            Assert.Equal(["Open file location", "Copy path"], headers);
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
        Assert.Equal(new Thickness(1, 1, 1, 0), view.TopSection.BorderThickness);
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

            var filesY = detailView.Split.Frame.TranslatePoint(new Point(0, 0), windowDetail)!.Value.Y;
            var stagedSection = windowStaging.GetVisualDescendants().OfType<Border>().First(b => Grid.GetRow(b) == 2);
            var stagedY = stagedSection.TranslatePoint(new Point(0, 0), windowStaging)!.Value.Y;

            Assert.Equal(filesY, stagedY, 1);

            // Drag detail pane's divider handle down by 40 px
            var handle = detailView.Split.DividerHandle;
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

            Assert.True(view.ViewModel.IsPathMode);
            var scroller = FilesScroller(view);
            Assert.Equal(ScrollBarVisibility.Auto, scroller.HorizontalScrollBarVisibility);
            Assert.True(scroller.Extent.Width > scroller.Viewport.Width);

            var pathTextBlock = view.FilesList.GetVisualDescendants().OfType<TextBlock>().First(tb => tb.Text == longPath);
            pathTextBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(pathTextBlock.Bounds.Width >= pathTextBlock.DesiredSize.Width - pathTextBlock.Margin.Left - 0.5);

            // Switch to Tree view
            Click(window, ModeButton(view, "Tree"));

            Assert.True(view.ViewModel.IsTreeMode);
            Assert.True(scroller.Extent.Width > scroller.Viewport.Width);
            var treeTextBlock = view.FilesList.GetVisualDescendants().OfType<TextBlock>().First(tb => tb.Text == longName);
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

            var scroller = FilesScroller(view);
            Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 0.5);

            var rowBorder = view.FilesList.GetVisualDescendants()
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

            var realizedGrids = view.FilesList.GetVisualDescendants()
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
    public async Task Five_thousand_files_realize_fewer_than_200_row_containers_in_both_modes()
    {
        await UsePathViewAsync();
        var headId = _fixture.CreateCommitWithManyFiles(5000, folders: 40);
        var view = new CommitDetailPanelView(_sp, _context, headId);
        var window = new Window { Width = 600, Height = 600, Content = view };
        window.Show();
        try
        {
            await WaitForCommitLoadedAsync(view);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.IsType<VirtualizingStackPanel>(view.FilesList.ItemsPanelRoot);
            Assert.Equal(5000, view.ViewModel.Rows.Count);
            var pathContainers = view.FilesList.GetVisualDescendants().OfType<ListBoxItem>().Count();
            Assert.InRange(pathContainers, 1, 199);

            Click(window, ModeButton(view, "Tree"));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(5040, view.ViewModel.Rows.Count);
            var treeContainers = view.FilesList.GetVisualDescendants().OfType<ListBoxItem>().Count();
            Assert.InRange(treeContainers, 1, 199);
        }
        finally
        {
            await UsePathViewAsync();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Showing_another_commit_replaces_the_header_and_the_files()
    {
        await UsePathViewAsync();
        var firstId = _fixture.RootCommitId;
        var view = new CommitDetailPanelView(_sp, _context, firstId);
        var window = new Window { Width = 600, Height = 600, Content = view };
        window.Show();
        try
        {
            await WaitForCommitLoadedAsync(view);
            Assert.Empty(ParentHashes(view));

            view.SetCommit(_fixture.HeadCommitId);
            await WaitForCommitLoadedAsync(view);
            Dispatcher.UIThread.RunJobs();

            Assert.Single(ParentHashes(view));
            Assert.Equal(_fixture.HeadCommitId.ToShortString(10), view.HashText.Text);
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

    private static bool IsUnderlined(TextBlock text)
        => text.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Underline) == true;

    private static IReadOnlyList<TextBlock> ParentHashes(CommitDetailPanelView view)
        => [.. view.ParentsList.GetVisualDescendants().OfType<TextBlock>().Where(IsUnderlined)];

    private static ScrollViewer FilesScroller(CommitDetailPanelView view)
        => view.FilesList.GetVisualDescendants().OfType<ScrollViewer>().First();

    private static Button ModeButton(CommitDetailPanelView view, string label)
        => view.GetVisualDescendants().OfType<Button>().First(b => b.Content is string text && text == label);

    private static IEnumerable<Border> RowBorders(CommitDetailPanelView view)
        => view.FilesList.GetVisualDescendants().OfType<Border>().Where(b => b.DataContext is CommitRowViewModel && b.Child is Grid);

    private static List<string> RowTexts(CommitDetailPanelView view)
        => [.. RowBorders(view).Select(b => ((TextBlock)((Grid)b.Child!).Children[^1]).Text!)];

    private static void Click(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, center, MouseButton.Left);
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private Task UsePathViewAsync() =>
        _sp.GetRequiredService<ISettingsService>().UpdateAsync(s => s with { CommitFilesView = CommitFilesView.Path });

    private static async Task WaitForCommitLoadedAsync(CommitDetailPanelView view)
    {
        for (var i = 0; i < 100; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (view.ViewModel.HasDetail)
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

        public GitObjectId CreateCommitWithManyFiles(int count, int folders = 0)
        {
            for (var i = 0; i < count; i++)
            {
                var dir = folders > 0 ? System.IO.Path.Combine(Path, $"dir_{i % folders:D2}") : Path;
                Directory.CreateDirectory(dir);
                File.WriteAllText(System.IO.Path.Combine(dir, $"file_{i:D4}.txt"), $"{i}\n");
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
