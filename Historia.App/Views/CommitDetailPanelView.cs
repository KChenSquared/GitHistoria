using Avalonia.Controls.Templates;
using LibGit2Sharp;
using Historia.Services.Git;
using Historia.Services.Settings;
using Historia.Services.State;
using static Historia.Views.ViewKit;

namespace Historia.Views;

/// <summary>Selected-commit detail: metadata, changed files, file diff (V4 port of CommitDetailPanel.razor).</summary>
public sealed class CommitDetailPanelView : UserControl, IDisposable
{
    private readonly RepositoryContext _ctx;
    private ObjectId _id;
    private readonly INotificationService _notifications;
    private readonly ISettingsService _settings;
    private readonly Historia.Services.UI.IExternalLauncher _launcher;

    private readonly StackPanel _head = new() { Spacing = 4 };
    private readonly ContentControl _filesHost = new();
    private readonly StackPanel _filesToggleRow = new()
        { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(12, 8, 12, 0) };
    private readonly TextBlock _statusMsg = Text("", "ColorTextMuted", 12);
    private int _parentIndex = 1;
    private bool _renderBody = true;
    private CancellationTokenSource? _loadCts;
    private bool _disposed;

    private IReadOnlyList<FileStatusEntry> _files = [];
    private Dictionary<string, List<FileStatusEntry>>? _treeByFolder;
    private Dictionary<string, SortedSet<string>>? _treeChildFolders;
    // v8 M13: which folder tree rows are collapsed (session-only, keyed by folder path).
    private readonly HashSet<string> _collapsedFolders = new(StringComparer.Ordinal);

    private int DetailFontSize => Math.Clamp(_settings.Current.CommitDetailFontSize, 10, 24);

    public CommitDetailPanelView(IServiceProvider sp, RepositoryContext ctx, ObjectId id)
    {
        _ctx = ctx;
        _id = id;
        _notifications = sp.GetRequiredService<INotificationService>();
        _settings = sp.GetRequiredService<ISettingsService>();
        _launcher = sp.GetRequiredService<Historia.Services.UI.IExternalLauncher>();

        var header = new StackPanel { Spacing = 8, Margin = new Thickness(12, 12, 12, 0) };
        var back = TextButton("← Working copy", () => _ctx.State.SetSelectedCommit(null));
        header.Children.Add(back);
        header.Children.Add(_head);
        header.Children.Add(_statusMsg);

        var filesScroll = new ScrollViewer
        {
            Content = _filesHost,
            Margin = new Thickness(12, 4, 12, 12)
        };

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*")
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(_filesToggleRow, 1);
        Grid.SetRow(filesScroll, 2);
        root.Children.Add(header);
        root.Children.Add(_filesToggleRow);
        root.Children.Add(filesScroll);

        // The diff itself renders in the centre panel (MainWindow.ApplyCenter listens for
        // OpenDiffChanged); this pane only lists the commit's changed files.
        Content = root;

        QueueLoad();
    }

    public void SetCommit(ObjectId id)
    {
        if (_id == id)
            return;

        _id = id;
        _parentIndex = 1;
        QueueLoad();
    }

    private void QueueLoad()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        _head.Children.Clear();
        _head.Children.Add(Text($"Commit {_id.Sha[..10]}", "ColorText", DetailFontSize + 1, FontWeight.SemiBold));
        _statusMsg.Text = "Loading file changes…";
        _statusMsg.IsVisible = true;
        _filesToggleRow.Children.Clear();
        _filesHost.Content = null;
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
            var currentParent = _parentIndex;
            var detail = await _ctx.Gateway.GetCommitDetailAsync(currentId, ct).ConfigureAwait(false);
            var files = await _ctx.Gateway.GetCommitFilesAsync(currentId, currentParent, ct).ConfigureAwait(false);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested || _id != currentId) return;

                var rules = _settings.Current.IssueAutolinks;
                var fs = DetailFontSize;
                _head.Children.Clear();
                _head.Children.Add(Autolinked(detail.MessageShort, rules, _launcher, fs + 1, "ColorText"));
                _head.Children.Add(BuildMetaRow(detail));
                var bodyText = (detail.MessageBody ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(bodyText))
                {
                    var bodyHost = new ContentControl();
                    void ShowBody(bool rendered) => bodyHost.Content = rendered
                        ? new MarkdownView(bodyText, _launcher)
                        : Autolinked(bodyText, rules, _launcher, fs, "ColorText");

                    var raw = new Button { Content = "Raw", Padding = new Thickness(6, 2), Background = Brushes.Transparent, BorderThickness = default };
                    var rend = new Button { Content = "Rendered", Padding = new Thickness(6, 2), Background = Brushes.Transparent, BorderThickness = default };
                    void SetToggle(bool rendered)
                    {
                        rend.FontWeight = rendered ? FontWeight.SemiBold : FontWeight.Normal;
                        raw.FontWeight = rendered ? FontWeight.Normal : FontWeight.SemiBold;
                        ShowBody(rendered);
                    }
                    raw.Click += (_, _) => SetToggle(false);
                    rend.Click += (_, _) => SetToggle(true);

                    var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                    tools.Children.Add(raw);
                    tools.Children.Add(rend);
                    _head.Children.Add(tools);
                    _head.Children.Add(bodyHost);
                    SetToggle(_renderBody);
                    raw.Click += (_, _) => _renderBody = false;
                    rend.Click += (_, _) => _renderBody = true;
                }

                // Merge commit: a parent selector — the diff is against the chosen parent.
                if (detail.ParentIds.Count > 1)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    row.Children.Add(Text("Merge — diff vs", "ColorTextMuted", 12));
                    for (var i = 1; i <= detail.ParentIds.Count; i++)
                    {
                        var idx = i;
                        var btn = new Button
                        {
                            Content = $"parent {i}",
                            Padding = new Thickness(8, 2),
                            FontWeight = idx == _parentIndex ? FontWeight.SemiBold : FontWeight.Normal,
                        };
                        btn.Click += (_, _) =>
                        {
                            _parentIndex = idx;
                            _ctx.State.SetOpenDiff(null);
                            QueueLoad();
                        };
                        row.Children.Add(btn);
                    }
                    _head.Children.Add(row);
                }

                _files = files;
                BuildTreeCache(files);
                _statusMsg.IsVisible = files.Count == 0;
                _statusMsg.Text = files.Count == 0 ? "No file changes." : "";
                BuildFilesToggle();
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
    /// Author + date + hash line. The author's email shows only as a tooltip on the name
    /// (not inline); date and hash are right-aligned, and the hash is a click-to-copy control
    /// styled brighter and underlined to read as clickable.
    /// </summary>
    private Control BuildMetaRow(CommitDetail detail)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var author = Text(detail.AuthorName, "ColorTextMuted", 12);
        ToolTip.SetTip(author, detail.AuthorEmail);
        Grid.SetColumn(author, 0);
        row.Children.Add(author);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        right.Children.Add(Text($"{detail.AuthorWhen:yyyy-MM-dd HH:mm}", "ColorTextMuted", 12));

        var hash = Text(detail.Id.Sha[..10], "ColorAccent", 12);
        hash.TextDecorations = TextDecorations.Underline;
        hash.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        ToolTip.SetTip(hash, "Click to copy full commit hash");
        var fullSha = detail.Id.Sha;
        hash.PointerPressed += (_, _) =>
            _ = TopLevel.GetTopLevel(hash)?.Clipboard?.SetTextAsync(fullSha);
        right.Children.Add(hash);

        Grid.SetColumn(right, 1);
        row.Children.Add(right);
        return row;
    }

    // ── v8 M13: Path / Tree file views ──

    private void BuildFilesToggle()
    {
        _filesToggleRow.Children.Clear();
        if (_files.Count == 0)
            return;

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

        _filesToggleRow.Children.Add(Text("Files", "ColorTextMuted", 12));
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
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Height = DetailFontSize + 8 };
        var chev = Text(collapsed ? "▸" : "▾", "ColorTextMuted", DetailFontSize - 2);
        chev.Margin = new Thickness(12 + depth * 14, 0, 0, 0);
        g.Children.Add(chev);
        var label = Text(name + "/", "ColorTextMuted", DetailFontSize, FontWeight.SemiBold);
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
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Height = DetailFontSize + 8 };
        var kind = Text(KindGlyph(f.Kind), "ColorTextMuted", DetailFontSize);
        kind.Margin = new Thickness(12 + indent * 14 + (indent > 0 ? 14 : 0), 0, 0, 0);
        row.Children.Add(kind);
        var p = Text(display, "ColorText", DetailFontSize);
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
            ParentIndex = _parentIndex,
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
        _loadCts?.Cancel();
        _loadCts?.Dispose();
    }
}

/// <summary>
/// File diff view (V4 port of DiffViewer.razor). Unified or side-by-side, per-token syntax
/// highlighting, old/new line numbers. Toggle in the header; default from settings.
/// </summary>
public sealed class DiffView : UserControl
{
    private static readonly FontFamily Mono = new("Consolas, Menlo, monospace");

    private readonly RepositoryContext _ctx;
    private readonly DiffRequest _request;
    private readonly ContentControl _host = new();
    private readonly Historia.Services.PowerTools.IPatchService? _patches;
    private readonly Historia.Services.Notifications.INotificationService? _notifications;
    private FileDiff? _diff;
    private bool _sideBySide;

    public DiffView(RepositoryContext ctx, DiffRequest request)
        : this(ctx, request, null, null) { }

    public DiffView(RepositoryContext ctx, DiffRequest request, ISettingsService? settings)
        : this(ctx, request, settings, null) { }

    public DiffView(RepositoryContext ctx, DiffRequest request, ISettingsService? settings, IServiceProvider? sp)
    {
        _ctx = ctx;
        _request = request;
        _sideBySide = settings?.Current.DefaultDiffView == DiffViewMode.SideBySide;
        _patches = sp?.GetService(typeof(Historia.Services.PowerTools.IPatchService)) as Historia.Services.PowerTools.IPatchService;
        _notifications = sp?.GetService(typeof(Historia.Services.Notifications.INotificationService)) as Historia.Services.Notifications.INotificationService;
        this.Dyn(BackgroundProperty, "ColorBg");

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Height = 30, Margin = new Thickness(12, 4) };
        header.Children.Add(Text(request.Path, "ColorText", 12, FontWeight.SemiBold));
        var toggle = TextButton(_sideBySide ? "Unified" : "Side-by-side", () => { _sideBySide = !_sideBySide; Rebuild(); });
        Grid.SetColumn(toggle, 1);
        header.Children.Add(toggle);
        var close = TextButton("Close", () => ctx.State.SetOpenDiff(null));
        Grid.SetColumn(close, 2);
        header.Children.Add(close);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_host);
        Content = root;

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _diff = await _ctx.Gateway.GetFileDiffAsync(_request, _ctx.Lifetime);
            Rebuild();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _host.Content = Text($"Could not load diff: {ex.Message}", "ColorDanger", 12);
        }
    }

    private void Rebuild()
    {
        if (_diff is null) return;
        if (_diff.IsBinary) { _host.Content = new TextBlock { Text = "Binary file — no textual diff.", Margin = new Thickness(12) }.Dyn(TextBlock.ForegroundProperty, "ColorTextMuted"); return; }
        if (_diff.Hunks.Count == 0) { _host.Content = new TextBlock { Text = "No changes.", Margin = new Thickness(12) }.Dyn(TextBlock.ForegroundProperty, "ColorTextMuted"); return; }

        _host.Content = new ScrollViewer
        {
            Content = _sideBySide ? BuildSideBySide(_diff) : BuildUnified(_diff),
            Padding = new Thickness(12, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
    }

    private bool CanStageHunks =>
        _patches is not null && _diff is { IsBinary: false }
        && _request.Source is DiffSource.UnstagedChanges or DiffSource.StagedChanges;

    private Control BuildUnified(FileDiff diff)
    {
        var panel = new StackPanel { Spacing = 0 };
        for (var hi = 0; hi < diff.Hunks.Count; hi++)
        {
            var hunk = diff.Hunks[hi];
            var hunkIndex = hi;
            var hdrRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            hdrRow.Children.Add(new TextBlock { Text = hunk.Header, FontFamily = Mono, FontSize = 12 }.Dyn(TextBlock.ForegroundProperty, "ColorAccent"));
            if (CanStageHunks)
            {
                var staging = _request.Source == DiffSource.UnstagedChanges;
                var btn = new Button
                {
                    Content = staging ? "Stage hunk" : "Unstage hunk",
                    Padding = new Thickness(6, 1),
                    FontSize = 11,
                    Background = Brushes.Transparent,
                    BorderThickness = default,
                };
                btn.Click += (_, _) => _ = ApplyHunkAsync(hunkIndex, staging);
                Grid.SetColumn(btn, 1);
                hdrRow.Children.Add(btn);
            }
            panel.Children.Add(hdrRow);

            // v8.4: every line used to be its own SelectableTextBlock, so a mouse drag could
            // only select within one line at a time and copy pulled just that line. The gutters
            // and per-line backgrounds stay one row per line (fixed pixel height, so they line
            // up with the text below), but the code itself is now a single SelectableTextBlock
            // spanning the whole hunk — one continuous selection, and Ctrl+C copies whatever
            // span the user dragged across, deletions included.
            const double lineH = 18;
            var lineGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("42,42,*"),
                RowDefinitions = new RowDefinitions(string.Join(",",
                    Enumerable.Repeat(lineH.ToString(System.Globalization.CultureInfo.InvariantCulture), hunk.Lines.Count))),
            };
            for (var li = 0; li < hunk.Lines.Count; li++)
            {
                var line = hunk.Lines[li];
                var bgKey = line.Type switch
                {
                    DiffLineType.Addition => "ColorDiffAddBg",
                    DiffLineType.Deletion => "ColorDiffDelBg",
                    _ => null,
                };
                var bg = new Border { Height = lineH };
                if (bgKey is not null) bg.Dyn(Border.BackgroundProperty, bgKey);
                Grid.SetRow(bg, li);
                Grid.SetColumnSpan(bg, 3);
                lineGrid.Children.Add(bg);

                var oldGutter = Gutter(line.OldLineNumber, 0);
                Grid.SetRow(oldGutter, li);
                lineGrid.Children.Add(oldGutter);
                var newGutter = Gutter(line.NewLineNumber, 1);
                Grid.SetRow(newGutter, li);
                lineGrid.Children.Add(newGutter);
            }

            var code = BuildHunkCode(hunk, diff.LanguageId, lineH);
            Grid.SetColumn(code, 2);
            Grid.SetRow(code, 0);
            Grid.SetRowSpan(code, Math.Max(1, hunk.Lines.Count));
            lineGrid.Children.Add(code);

            panel.Children.Add(lineGrid);
        }
        return panel;
    }

    /// <summary>One continuous, selectable block of monospace text for an entire hunk's code
    /// column — every line, in order, separated by <see cref="LineBreak"/>s and syntax-tokenized
    /// exactly like <see cref="HighlightedLine"/>, so a drag-select can span multiple lines.
    /// <paramref name="lineHeight"/> must match the fixed row height of the gutter/background
    /// grid this overlays, so text lines land exactly on their background rows.</summary>
    private static Control BuildHunkCode(DiffHunk hunk, string? lang, double lineHeight)
    {
        var tb = new SelectableTextBlock
        {
            FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.NoWrap, LineHeight = lineHeight,
        }.Dyn(SelectableTextBlock.ForegroundProperty, "ColorText");

        for (var i = 0; i < hunk.Lines.Count; i++)
        {
            var line = hunk.Lines[i];
            var prefix = line.Type switch { DiffLineType.Addition => "+ ", DiffLineType.Deletion => "- ", _ => "  " };
            AppendHighlightedInlines(tb.Inlines!, prefix + line.Content, lang, prefix.Length);
            if (i < hunk.Lines.Count - 1)
                tb.Inlines!.Add(new Avalonia.Controls.Documents.LineBreak());
        }
        return tb;
    }

    private async Task ApplyHunkAsync(int hunkIndex, bool stage)
    {
        if (_patches is null || _diff is null) return;
        try
        {
            var selected = new HashSet<DiffLineRef> { new(hunkIndex, 0) };
            var patch = _patches.GeneratePatch(_diff, PatchScope.SingleHunk, selected);
            if (string.IsNullOrEmpty(patch))
                return;

            var reverse = !stage; // unstage = reverse-apply against the index
            var check = await _patches.CheckAsync(_ctx, patch, PatchTarget.StagingArea, reverse, _ctx.Lifetime);
            if (!check.CanApply)
            {
                _notifications?.Publish(Historia.Services.Notifications.NotificationSeverity.Warning,
                    $"Cannot {(stage ? "stage" : "unstage")} this hunk: {check.FailureReason}");
                return;
            }
            await _patches.ApplyPatchAsync(_ctx, patch, PatchTarget.StagingArea, reverse, _ctx.Lifetime);
            await _ctx.State.RefreshStatusAsync(_ctx.Lifetime);
            _diff = await _ctx.Gateway.GetFileDiffAsync(_request, _ctx.Lifetime);
            Rebuild();
        }
        catch (Exception ex)
        {
            _notifications?.Publish(Historia.Services.Notifications.NotificationSeverity.Error, ex.Message, ex.NotificationDetails());
        }
    }

    private Control BuildSideBySide(FileDiff diff)
    {
        var left = new StackPanel { Spacing = 0 };
        var right = new StackPanel { Spacing = 0 };
        foreach (var hunk in diff.Hunks)
        {
            var hdr = new TextBlock { Text = hunk.Header, FontFamily = Mono, FontSize = 12 }.Dyn(TextBlock.ForegroundProperty, "ColorAccent");
            left.Children.Add(hdr);
            right.Children.Add(new TextBlock { Text = " ", FontFamily = Mono, FontSize = 12 });
            foreach (var line in hunk.Lines)
            {
                switch (line.Type)
                {
                    case DiffLineType.Deletion:
                        left.Children.Add(SideRow(line.OldLineNumber, line.Content, diff.LanguageId, "ColorDiffDelBg"));
                        right.Children.Add(SideRow(null, "", diff.LanguageId, null));
                        break;
                    case DiffLineType.Addition:
                        left.Children.Add(SideRow(null, "", diff.LanguageId, null));
                        right.Children.Add(SideRow(line.NewLineNumber, line.Content, diff.LanguageId, "ColorDiffAddBg"));
                        break;
                    default:
                        left.Children.Add(SideRow(line.OldLineNumber, line.Content, diff.LanguageId, null));
                        right.Children.Add(SideRow(line.NewLineNumber, line.Content, diff.LanguageId, null));
                        break;
                }
            }
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,*") };
        grid.Children.Add(left);
        var sep = new Border { Width = 1 }.Dyn(Border.BackgroundProperty, "ColorBorder");
        Grid.SetColumn(sep, 1);
        grid.Children.Add(sep);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    private Control SideRow(int? lineNo, string content, string? lang, string? bgKey)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("42,*") };
        row.Children.Add(Gutter(lineNo, 0));
        var code = HighlightedLine(content, lang, 0);
        Grid.SetColumn(code, 1);
        row.Children.Add(code);
        var b = new Border { Child = row };
        if (bgKey is not null) b.Dyn(Border.BackgroundProperty, bgKey);
        return b;
    }

    private static Control Gutter(int? n, int col)
    {
        var t = new TextBlock
        {
            Text = n?.ToString() ?? "",
            FontFamily = Mono,
            FontSize = 11,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        }.Dyn(TextBlock.ForegroundProperty, "ColorTextFaint");
        Grid.SetColumn(t, col);
        return t;
    }

    private static Control HighlightedLine(string text, string? lang, int prefixLen)
    {
        var tb = new SelectableTextBlock { FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.NoWrap }
            .Dyn(SelectableTextBlock.ForegroundProperty, "ColorText");
        AppendHighlightedInlines(tb.Inlines!, text, lang, prefixLen);
        return tb;
    }

    private static void AppendHighlightedInlines(Avalonia.Controls.Documents.InlineCollection inlines,
        string text, string? lang, int prefixLen)
    {
        if (prefixLen > 0)
            inlines.Add(new Avalonia.Controls.Documents.Run(text[..prefixLen]));

        var code = text[prefixLen..];
        var tokens = Historia.Services.Diff.SyntaxHighlighter.Tokenize(code, lang);
        var pos = 0;
        foreach (var tok in tokens)
        {
            if (tok.Start > pos)
                inlines.Add(new Avalonia.Controls.Documents.Run(code[pos..tok.Start]));
            var run = new Avalonia.Controls.Documents.Run(code.Substring(tok.Start, tok.Length));
            var key = tok.Kind switch
            {
                Historia.Services.Diff.TokenKind.Keyword => "ColorPrimary",
                Historia.Services.Diff.TokenKind.String => "ColorGitAdded",
                Historia.Services.Diff.TokenKind.Comment => "ColorTextFaint",
                Historia.Services.Diff.TokenKind.Number => "ColorAccent",
                _ => "ColorText",
            };
            run.Bind(Avalonia.Controls.Documents.Run.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(key));
            inlines.Add(run);
            pos = tok.Start + tok.Length;
        }
        if (pos < code.Length)
            inlines.Add(new Avalonia.Controls.Documents.Run(code[pos..]));
    }
}
