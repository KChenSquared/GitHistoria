namespace Historia.Views;

/// <summary>
/// Selected-commit detail: metadata, parent commits, full raw commit message, and changed files.
/// The working copy return button resides in the repository top bar.
/// </summary>
public sealed class CommitDetailPanelView : UserControl, IDisposable
{
    private readonly RepositoryContext _ctx;
    private GitObjectId _id;
    private readonly INotificationService _notifications;
    private readonly ISettingsService _settings;
    private readonly Historia.Services.UI.IExternalLauncher _launcher;

    private readonly ContentControl _metaHost = new();
    private readonly ContentControl _parentsHost = new();
    private readonly ContentControl _messageHost = new();
    private readonly ContentControl _filesHost = new();
    private readonly StackPanel _filesToggleRow = new()
    { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly TextBlock _statusMsg = Text("", "ColorTextMuted", 12);
    private CancellationTokenSource? _loadCts;
    private bool _disposed;
    private CommitDetail? _detail;

    private IReadOnlyList<FileStatusEntry> _files = [];
    private Dictionary<string, List<FileStatusEntry>>? _treeByFolder;
    private Dictionary<string, SortedSet<string>>? _treeChildFolders;
    private readonly HashSet<string> _collapsedFolders = new(StringComparer.Ordinal);

    private const double AuthorGap = SectionFrame.HeaderGap + 4;
    private const double FileRowFontSize = 14;

    private readonly Border _authorRule;
    private readonly TextBlock _filesLabel;
    private TextBlock? _authorText;
    private TextBlock? _hashText;
    private TextBlock? _dateText;
    private StackPanel? _parentsList;

    internal ContentControl MetaHostForTesting => _metaHost;
    internal ContentControl ParentsHostForTesting => _parentsHost;
    internal ContentControl MessageHostForTesting => _messageHost;
    internal ContentControl FilesHostForTesting => _filesHost;
    internal ScrollViewer FilesScrollViewerForTesting { get; }
    internal Border TopSectionForTesting { get; }
    internal Control FilesSectionForTesting { get; }
    internal Control FilesDividerHandleForTesting { get; }
    internal TextBlock? AuthorTextForTesting => _authorText;
    internal TextBlock? HashTextForTesting => _hashText;
    internal TextBlock? DateTextForTesting => _dateText;
    internal StackPanel? ParentsListForTesting => _parentsList;
    internal Border AuthorRuleForTesting => _authorRule;
    internal TextBlock FilesLabelForTesting => _filesLabel;

    public CommitDetailPanelView(IServiceProvider sp, RepositoryContext ctx, GitObjectId id)
    {
        _ctx = ctx;
        _id = id;
        _notifications = sp.GetRequiredService<INotificationService>();
        _settings = sp.GetRequiredService<ISettingsService>();
        _launcher = sp.GetRequiredService<Historia.Services.UI.IExternalLauncher>();
        _settings.Changed += OnSettingsChanged;

        _statusMsg.HorizontalAlignment = HorizontalAlignment.Center;
        _statusMsg.VerticalAlignment = VerticalAlignment.Center;
        _statusMsg.Margin = new Thickness(0, 24, 0, 0);

        var topInner = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(SectionFrame.InnerSide, AuthorGap, SectionFrame.InnerSide, SectionFrame.InnerBottom),
        };
        DockPanel.SetDock(_metaHost, Dock.Top);
        DockPanel.SetDock(_parentsHost, Dock.Top);
        topInner.Children.Add(_metaHost);
        topInner.Children.Add(_parentsHost);

        _authorRule = new Border
        {
            Height = 1,
            Margin = new Thickness(0, AuthorGap, 0, SectionFrame.DividerBottomGap),
        }.Dyn(Border.BackgroundProperty, "ColorBorder");
        DockPanel.SetDock(_authorRule, Dock.Top);
        topInner.Children.Add(_authorRule);

        var messageScroll = new ScrollViewer
        {
            Content = _messageHost,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        topInner.Children.Add(messageScroll);

        var topSection = new Border
        {
            Child = topInner,
            BorderThickness = new Thickness(1, 1, 1, 0),
            MinHeight = SectionFrame.TopMinHeight,
        }.Dyn(Border.BorderBrushProperty, "ColorBorder");
        TopSectionForTesting = topSection;

        _filesLabel = Text("Files:", "ColorText", 16);
        var filesSection = SectionFrame.Build(
            left: _filesLabel,
            center: null,
            right: _filesToggleRow,
            body: _filesHost,
            border: new Thickness(1),
            minHeight: SectionFrame.BottomMinHeight,
            out var filesDividerHandle,
            horizontalScroll: true);
        FilesSectionForTesting = filesSection;
        FilesDividerHandleForTesting = filesDividerHandle;
        FilesScrollViewerForTesting = (ScrollViewer)(_filesHost.Parent ?? ((DockPanel)filesSection.Child!).Children[2]);

        var topRow = new RowDefinition(new GridLength(SectionFrame.GetSplitHeight(_ctx.Id), GridUnitType.Pixel))
        {
            MinHeight = SectionFrame.TopMinHeight
        };
        var handleRow = new RowDefinition(new GridLength(SectionFrame.HandleHeight, GridUnitType.Pixel));
        var bottomRow = new RowDefinition(new GridLength(1, GridUnitType.Star))
        {
            MinHeight = SectionFrame.BottomMinHeight
        };

        var root = new Grid
        {
            ClipToBounds = true,
            RowDefinitions = { topRow, handleRow, bottomRow }
        };

        Grid.SetRow(topSection, 0);
        root.Children.Add(topSection);

        var splitHandle = new Border
        {
            Height = SectionFrame.HandleHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
        };
        Grid.SetRow(splitHandle, 1);
        root.Children.Add(splitHandle);

        Grid.SetRow(filesSection, 2);
        root.Children.Add(filesSection);

        SectionFrame.AttachRowResize(splitHandle, root, topRow, h => SectionFrame.SetSplitHeight(_ctx.Id, h));
        SectionFrame.AttachRowResize(filesDividerHandle, root, topRow, h => SectionFrame.SetSplitHeight(_ctx.Id, h));

        AttachedToVisualTree += (_, _) => SectionFrame.ApplySplitHeight(root, topRow, _ctx.Id);
        root.SizeChanged += (_, _) => SectionFrame.ApplySplitHeight(root, topRow, _ctx.Id);

        Content = root;

        BuildFilesToggle();
        QueueLoad();
    }

    public void SetCommit(GitObjectId id)
    {
        if (_id == id)
            return;

        _id = id;
        QueueLoad();
    }

    private void QueueLoad()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        // Drop the previous commit so a settings-driven rebuild can't render it over this one.
        _detail = null;
        _metaHost.Content = null;
        _parentsHost.Content = null;
        _messageHost.Content = null;
        _statusMsg.Text = "Loading file changes…";
        _statusMsg.IsVisible = true;
        _filesHost.Content = _statusMsg;
        _files = [];

        _ = LoadAsync(ct);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            // Debounce tree diffing by 60ms to absorb rapid arrow-key navigation in graph
            await Task.Delay(60, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;

            var currentId = _id;
            var detail = await _ctx.Gateway.GetCommitDetailAsync(currentId, ct).ConfigureAwait(false);
            var files = await _ctx.Gateway.GetCommitFilesAsync(currentId, 1, ct).ConfigureAwait(false);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested || _disposed || _id != currentId) return;

                _detail = detail;
                var rules = _settings.Current.IssueAutolinks;
                _metaHost.Content = BuildMetaRow(detail);
                _parentsHost.Content = BuildParentsRow(detail);
                _messageHost.Content = Autolinked(CommitMessageReflow.Reflow(detail.Message), rules, _launcher, 14, "ColorText");

                _files = files;
                BuildTreeCache(files);
                _statusMsg.IsVisible = files.Count == 0;
                _statusMsg.Text = files.Count == 0 ? "No file changes." : "";
                BuildFilesToggle();
                if (files.Count == 0)
                    _filesHost.Content = _statusMsg;
                else
                    RenderFiles();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                _notifications.Publish(NotificationSeverity.Error, $"Could not load commit: {ex.Message}", ex.NotificationDetails());
        }
    }

    /// <summary>
    /// Author name and click-to-copy hash. Author email shows as a tooltip on the name.
    /// The commit date is shown on the parents row.
    /// </summary>
    private Control BuildMetaRow(CommitDetail detail)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var author = Text(detail.AuthorName, "ColorText", 16);
        ToolTip.SetTip(author, detail.AuthorEmail);
        Grid.SetColumn(author, 0);
        row.Children.Add(author);
        _authorText = author;

        var hash = Text(detail.Id.ToShortString(10), "ColorAccent", 14);
        hash.VerticalAlignment = VerticalAlignment.Center;
        hash.TextDecorations = TextDecorations.Underline;
        hash.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(hash, "Click to copy full commit hash");
        var fullSha = detail.Id.ToString();
        hash.PointerPressed += (_, _) =>
            _ = TopLevel.GetTopLevel(hash)?.Clipboard?.SetTextAsync(fullSha);
        Grid.SetColumn(hash, 1);
        row.Children.Add(hash);
        _hashText = hash;

        return row;
    }

    /// <summary>
    /// Date on the left (yyyy-MM-dd), parents list on the right.
    /// </summary>
    private Control BuildParentsRow(CommitDetail detail)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var displayedDate = CommitDateDisplay.Pick(detail.AuthorWhen, detail.CommitWhen, _settings.Current.ShowAuthorDate);
        var dateText = Text($"{displayedDate:yyyy-MM-dd}", "ColorTextMuted", 12);
        dateText.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(dateText, $"Committed {detail.CommitWhen:yyyy-MM-dd HH:mm}\nAuthored {detail.AuthorWhen:yyyy-MM-dd HH:mm}");
        Grid.SetColumn(dateText, 0);
        row.Children.Add(dateText);
        _dateText = dateText;

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _parentsList = right;
        right.Children.Add(Text("Parent(s):", "ColorTextMuted", 12));

        if (detail.ParentIds.Count == 0)
        {
            right.Children.Add(Text("none", "ColorTextMuted", 12));
        }
        else
        {
            foreach (var pid in detail.ParentIds)
            {
                var parentSha = pid.ToString();
                var shortSha = parentSha.Length >= 10 ? parentSha[..10] : parentSha;
                var hash = Text(shortSha, "ColorText", 12);
                hash.TextDecorations = TextDecorations.Underline;
                hash.Cursor = new Cursor(StandardCursorType.Hand);
                ToolTip.SetTip(hash, "Go to parent commit");
                hash.PointerPressed += (_, e) =>
                {
                    if (!e.GetCurrentPoint(hash).Properties.IsLeftButtonPressed)
                        return;
                    e.Handled = true;
                    _ctx.State.SetSelectedCommit(pid);
                    _ctx.State.RequestRevealCommit(pid);
                };
                right.Children.Add(hash);
            }
        }

        Grid.SetColumn(right, 1);
        row.Children.Add(right);
        return row;
    }

    private void BuildFilesToggle()
    {
        _filesToggleRow.Children.Clear();

        Button Seg(string label, CommitFilesView mode)
        {
            var active = _settings.Current.CommitFilesView == mode;
            var b = new Button
            {
                Content = label,
                Padding = new Thickness(8, 2),
                Background = Brushes.Transparent,
                BorderThickness = default,
                FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal,
            };
            b.Click += (_, _) =>
            {
                if (_settings.Current.CommitFilesView == mode) return;
                _ = _settings.UpdateAsync(s => s with { CommitFilesView = mode });
                BuildFilesToggle();
                RenderFiles();
            };
            return b;
        }

        _filesToggleRow.Children.Add(Seg("Path", CommitFilesView.Path));
        _filesToggleRow.Children.Add(Seg("Tree", CommitFilesView.Tree));
    }

    private ItemsControl? _treeItemsControl;

    private abstract record TreeRowItem;
    private sealed record TreeFolderItem(string FolderPath, string Name, int Depth, bool Collapsed) : TreeRowItem;
    private sealed record TreeFileItem(FileStatusEntry File, string Display, int Depth) : TreeRowItem;

    private void RenderFiles()
    {
        if (_settings.Current.CommitFilesView == CommitFilesView.Tree)
        {
            if (_filesHost.Content is ItemsControl { Tag: "Tree" } existing)
            {
                _treeItemsControl = existing;
                existing.ItemsSource = FlattenTree();
            }
            else
            {
                _filesHost.Content = BuildTreeView();
            }
        }
        else
        {
            _treeItemsControl = null;
            _filesHost.Content = BuildPathView();
        }
    }

    private Control BuildPathView()
    {
        var list = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemsSource = _files,
            ItemTemplate = new FuncDataTemplate<FileStatusEntry>((f, _) =>
                f is null ? null : FileLeafRow(f, f.Path, indent: 0)),
        };
        return list;
    }

    private void BuildTreeCache(IReadOnlyList<FileStatusEntry> files)
    {
        var byFolder = new Dictionary<string, List<FileStatusEntry>>(StringComparer.Ordinal);
        var childFolders = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var f in files)
        {
            var slash = f.Path.LastIndexOf('/');
            var folder = slash < 0 ? "" : f.Path[..slash];
            if (!byFolder.TryGetValue(folder, out var list))
            {
                list = [];
                byFolder[folder] = list;
            }
            list.Add(f);

            // register every ancestor → child-folder edge
            var parts = folder.Length == 0 ? [] : folder.Split('/');
            var acc = "";
            foreach (var seg in parts)
            {
                var next = acc.Length == 0 ? seg : acc + "/" + seg;
                if (!childFolders.TryGetValue(acc, out var cs))
                {
                    cs = new(StringComparer.Ordinal);
                    childFolders[acc] = cs;
                }
                cs.Add(next);
                acc = next;
            }
        }

        foreach (var list in byFolder.Values)
            list.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.Ordinal));

        _treeByFolder = byFolder;
        _treeChildFolders = childFolders;
    }

    private List<TreeRowItem> FlattenTree()
    {
        var items = new List<TreeRowItem>();
        if (_treeByFolder is null || _treeChildFolders is null)
            return items;

        void Emit(string folder, int depth)
        {
            if (_treeChildFolders.TryGetValue(folder, out var subs))
            {
                foreach (var sub in subs)
                {
                    var name = sub[(sub.LastIndexOf('/') + 1)..];
                    var collapsed = _collapsedFolders.Contains(sub);
                    items.Add(new TreeFolderItem(sub, name, depth, collapsed));
                    if (!collapsed)
                        Emit(sub, depth + 1);
                }
            }

            if (_treeByFolder.TryGetValue(folder, out var files))
            {
                foreach (var f in files)
                    items.Add(new TreeFileItem(f, f.Path[(f.Path.LastIndexOf('/') + 1)..], depth));
            }
        }

        Emit("", 0);
        return items;
    }

    private Control BuildTreeView()
    {
        _treeItemsControl = new ItemsControl
        {
            Tag = "Tree",
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemsSource = FlattenTree(),
            ItemTemplate = new FuncDataTemplate<TreeRowItem>((item, _) => item switch
            {
                TreeFolderItem folder => FolderRow(folder.FolderPath, folder.Name, folder.Depth, folder.Collapsed),
                TreeFileItem file => FileLeafRow(file.File, file.Display, file.Depth),
                _ => null,
            }),
        };
        return _treeItemsControl;
    }

    private Control FolderRow(string folderPath, string name, int depth, bool collapsed)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Height = FileRowFontSize + 8 };
        var chev = Text(collapsed ? "▸" : "▾", "ColorTextMuted", FileRowFontSize - 2);
        chev.Margin = new Thickness(12 + depth * 14, 0, 0, 0);
        g.Children.Add(chev);
        var label = Text(name + "/", "ColorTextMuted", FileRowFontSize, FontWeight.SemiBold);
        label.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(label, 1);
        g.Children.Add(label);
        var b = new Border { Child = g, Background = Brushes.Transparent, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        b.PointerPressed += (_, _) =>
        {
            if (!_collapsedFolders.Add(folderPath))
                _collapsedFolders.Remove(folderPath);
            if (_treeItemsControl is not null)
                _treeItemsControl.ItemsSource = FlattenTree();
            else
                RenderFiles();
        };
        return b;
    }

    private Control FileLeafRow(FileStatusEntry f, string display, int indent)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Height = FileRowFontSize + 8 };
        var kind = Text(KindGlyph(f.Kind), "ColorTextMuted", FileRowFontSize);
        kind.Margin = new Thickness(12 + indent * 14 + (indent > 0 ? 14 : 0), 0, 0, 0);
        row.Children.Add(kind);
        var p = Text(display, "ColorText", FileRowFontSize);
        p.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(p, 1);
        row.Children.Add(p);

        var b = new Border { Child = row, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), Background = Brushes.Transparent };
        var entryPath = f.Path;
        b.WithRevealMenu(_launcher, () => System.IO.Path.Combine(_ctx.RepositoryPath, entryPath));
        b.PointerPressed += (_, _) => _ctx.State.SetOpenDiff(new DiffRequest
        {
            Source = DiffSource.Commit,
            Path = entryPath,
            CommitId = _id,
            ParentIndex = 1,
            ContextLines = _settings.Current.DiffContextLines,
        });
        return b;
    }

    private static string KindGlyph(FileChangeKind kind) => kind switch
    {
        FileChangeKind.Added => "A",
        FileChangeKind.Modified => "M",
        FileChangeKind.Deleted => "D",
        FileChangeKind.Renamed => "R",
        FileChangeKind.Copied => "C",
        FileChangeKind.TypeChanged => "T",
        FileChangeKind.Untracked => "U",
        FileChangeKind.Ignored => "I",
        FileChangeKind.Conflicted => "!",
        _ => "?",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.NewSettings.ShowAuthorDate != e.OldSettings.ShowAuthorDate)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || _detail is null)
                    return;
                _metaHost.Content = BuildMetaRow(_detail);
                _parentsHost.Content = BuildParentsRow(_detail);
            });
        }
    }
}

