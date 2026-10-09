namespace Historia.Views;

/// <summary>
/// Working tree, staging area and commit box. Changes (unstaged) and Staged are the two
/// resizable sections, with a checkbox column on each and a green ⬇ Stage / red ⬆ Unstage
/// arrow pair in the Staged header acting on the checked rows; both the Staged section's
/// top border and the rule under the Staged header resize the split. The commit-message strip
/// is fixed size and pinned to the bottom, below both — it doesn't trade space with either list.
/// </summary>
public sealed class StagingPanelView : UserControl, IDisposable
{
    // Per-repo commit-message draft, kept for the session.
    private static readonly Dictionary<string, string> DraftByRepo = new(StringComparer.Ordinal);

    private readonly RepositoryContext _ctx;
    private readonly INotificationService _notifications;
    private readonly IDialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly IKeyboardCommandRouter _keyboard;
    private readonly Historia.Services.UI.IExternalLauncher _launcher;
    private readonly Historia.Services.PowerTools.IStashService _stash;
    private readonly IRepoActionBus _actionBus;
    private readonly IAiProfileService? _aiProfiles;
    private readonly IAiIntegrationsPanel? _aiPanel;
    private readonly ICommitMessageGenerator? _aiGenerator;
    private Button? _wand;
    private Button? _aiSettings;
    private EventHandler? _onAiChanged;
    private CancellationTokenSource? _generateCts;
    private bool _disposed;
    private IDisposable? _commitShortcut;

    // A commit message offered by an external tool (a coding agent) that the box did not take.
    private Border? _offerBar;
    private TextBlock? _offerText;
    private Button? _offerUse;
    private Button? _offerDismiss;
    private string? _offered;
    private TextBlock? _agentNote;
    private string? _agentNoteText;

    private readonly TextBox _message = new()
    {
        AcceptsReturn = true,
        Height = 96,
        Watermark = "Commit message (subject; blank line; body)",
        TextWrapping = TextWrapping.Wrap,
        Padding = new Thickness(6, 6, 6, 20), // reserve space for the in-box counter
    };
    private readonly TextBox _author = new() { Watermark = "Author override (Name <email>)" };
    private readonly CheckBox _amend = new()
    {
        Content = "Amend",
        FontSize = CommitOptionsFontSize,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _counter = Text("", "ColorTextMuted", 11);
    private readonly Button _commit;
    private readonly Button _stashStaged;

    private readonly StackPanel _changesRows = new() { Spacing = 1 };
    private readonly StackPanel _stagedRows = new() { Spacing = 1 };
    // Header labels and the bulk Stage-all / Unstage-all buttons share one font size so the
    // section-header row reads as one line.
    private const double HeaderRowFontSize = 13;
    private readonly TextBlock _changesHeader = Text("", "ColorTextMuted", HeaderRowFontSize, FontWeight.SemiBold);
    private readonly TextBlock _stagedHeader = Text("", "ColorTextMuted", HeaderRowFontSize, FontWeight.SemiBold);
    private readonly CheckBox _selectAllChanges = new();
    private readonly CheckBox _selectAllStaged = new();
    private readonly Button _stageArrow;
    private readonly Button _unstageArrow;
    private readonly Button _discardAllChanges;
    private readonly Dictionary<string, Border> _conflictedRowCache = new(StringComparer.Ordinal);

    internal StackPanel ChangesRowsForTesting => _changesRows;
    internal StackPanel StagedRowsForTesting => _stagedRows;
    internal Button DiscardAllChangesForTesting => _discardAllChanges;
    internal Task DiscardAllChangesForTestingAsync() => DiscardAllChangesAsync();
    internal TextBlock ChangesHeaderForTesting => _changesHeader;
    internal IReadOnlyDictionary<string, Border> ConflictedRowCacheForTesting => _conflictedRowCache;
    internal TextBox MessageForTesting => _message;
    internal CheckBox AmendForTesting => _amend;
    internal Button? WandForTesting => _wand;
    internal Button? AiSettingsForTesting => _aiSettings;
    internal Panel? MessageCellForTesting { get; private set; }
    internal Grid? CommitOptionsRowForTesting { get; private set; }
    internal bool IsGeneratingForTesting => _generateCts is not null;
    internal Border? OfferBarForTesting => _offerBar;
    internal Button? OfferUseForTesting => _offerUse;
    internal Button? OfferDismissForTesting => _offerDismiss;
    internal TextBlock? AgentNoteForTesting => _agentNote;
    internal Task OfferUseForTestingAsync() => UseOfferAsync();

    /// <summary>The generation in flight, if any; completes when it has finished and the box is editable again.</summary>
    internal Task GenerationForTesting { get; private set; } = Task.CompletedTask;

    /// <summary>The Ctrl+Enter commit path, callable without a keyboard.</summary>
    internal Task CommitForTestingAsync() => CommitAsync(false);

    // Checked paths per section, preserved across StatusChanged re-renders where the path still exists.
    private readonly HashSet<string> _checkedChanges = new(StringComparer.Ordinal);
    private readonly HashSet<string> _checkedStaged = new(StringComparer.Ordinal);

    private RepositoryStatus _status = RepositoryStatus.Empty;
    private bool _committing;
    private bool _stashing;
    private bool _suppressSelectAll;

    // Matches RefNavigatorView.SectionMaxHeight — the commit-message section caps its
    // content at the same height as the left-hand ref navigator's sections, so a long message
    // scrolls internally instead of growing the message strip without bound.
    private const double SectionMaxHeight = 240;

    public StagingPanelView(IServiceProvider sp, RepositoryContext ctx)
    {
        _ctx = ctx;
        _notifications = sp.GetRequiredService<INotificationService>();
        _dialogs = sp.GetRequiredService<IDialogService>();
        _settings = sp.GetRequiredService<ISettingsService>();
        _keyboard = sp.GetRequiredService<IKeyboardCommandRouter>();
        _launcher = sp.GetRequiredService<Historia.Services.UI.IExternalLauncher>();
        _stash = sp.GetRequiredService<Historia.Services.PowerTools.IStashService>();
        _actionBus = sp.GetRequiredService<IRepoActionBus>();
        _aiProfiles = sp.GetService<IAiProfileService>();
        _aiPanel = sp.GetService<IAiIntegrationsPanel>();
        _aiGenerator = sp.GetService<ICommitMessageGenerator>();

        _commit = TextButton("Commit", () => _ = CommitAsync(false), primary: true);
        var commitPush = TextButton("Commit & Push", () => _ = CommitAsync(true));
        _stashStaged = TextButton("Stash Staged", () => _ = StashStagedAsync());
        ToolTip.SetTip(_stashStaged, "Stash only the staged changes, leaving unstaged edits in the working tree");

        _message.Text = DraftByRepo.GetValueOrDefault(ctx.Id, "");
        ScrollViewer.SetVerticalScrollBarVisibility(_message, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        _message.TextChanged += (_, _) =>
        {
            UpdateCounter();
            if (_agentNote is { IsVisible: true } && _message.Text != _agentNoteText)
                HideAgentNote();
        };
        UpdateCounter();
        _commitShortcut = _keyboard.Register("Ctrl+Enter", () => CommitAsync(false));

        _selectAllChanges.IsThreeState = true;
        _selectAllStaged.IsThreeState = true;
        _selectAllChanges.Click += (_, _) => ToggleSelectAll(staged: false);
        _selectAllStaged.Click += (_, _) => ToggleSelectAll(staged: true);

        _stageArrow = ArrowButton("arrow_downward", "ColorSuccess", "Stage", () => _ = MoveCheckedAsync(stage: true));
        _unstageArrow = ArrowButton("arrow_upward", "ColorDanger", "Unstage", () => _ = MoveCheckedAsync(stage: false));

        // Glyph sized just above the 13 px header-row text so the bordered delete pill matches
        // the Stage-all / Unstage-all pill height.
        var deleteGlyph = IconGlyphPath("delete", 14, "ColorDanger");
        _discardAllChanges = new Button
        {
            Content = deleteGlyph,
            Padding = new Thickness(6, 2),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _discardAllChanges.Classes.Add("danger");
        _discardAllChanges.Dyn(Button.BorderBrushProperty, "ColorDanger");
        ToolTip.SetTip(_discardAllChanges, "Discard all changes");
        _discardAllChanges.Click += (_, e) => { e.Handled = true; _ = DiscardAllChangesAsync(); };

        // ── message box + in-box counter ──
        var messageCell = new Grid();
        messageCell.Children.Add(_message);
        _counter.HorizontalAlignment = HorizontalAlignment.Right;
        _counter.VerticalAlignment = VerticalAlignment.Bottom;
        _counter.Margin = new Thickness(0, 0, 8, 4);
        _counter.IsHitTestVisible = false;
        messageCell.Children.Add(_counter);
        MessageCellForTesting = messageCell;
        BuildAgentProposalViews();

        var optionsRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        };
        if (_aiProfiles is not null && _aiPanel is not null && _aiGenerator is not null)
        {
            var aiStrip = BuildAiButtons();
            Grid.SetColumn(aiStrip, 0);
            optionsRow.Children.Add(aiStrip);
        }
        Grid.SetColumn(_amend, 2);
        optionsRow.Children.Add(_amend);
        CommitOptionsRowForTesting = optionsRow;

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_commit);
        actions.Children.Add(commitPush);
        actions.Children.Add(_stashStaged);

        var messageBox = new StackPanel { Spacing = 6, Margin = new Thickness(12, 8, 12, 12) };
        messageBox.Children.Add(_offerBar!);
        messageBox.Children.Add(messageCell);
        messageBox.Children.Add(_agentNote!);
        messageBox.Children.Add(optionsRow);
        messageBox.Children.Add(_author);
        messageBox.Children.Add(actions);
        var messageSection = new Border
        {
            Child = new ScrollViewer
            {
                Content = messageBox,
                MaxHeight = SectionMaxHeight,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            },
            MinHeight = 64,
            BorderThickness = new Thickness(1),
        }.Dyn(Border.BorderBrushProperty, "ColorBorder");

        // ── layout ──────────────────────────────────────────────────────────────────────────
        // A DockPanel keeps each piece in its own lane so a drag can never push one off-screen:
        //   • the commit-message strip docks to the bottom at its own (capped) content height —
        //     always pinned there, never trading space with the lists,
        //   • the Changes / Staged split fills whatever is left.
        // Inside that fill area a 3-row grid (Auto / Auto / *) holds Changes in a fixed-pixel
        // row that the splitter resizes, a thin splitter row, then Staged in the Star row that
        // simply absorbs the remainder. Changes is therefore pinned to the top and is the only
        // section the drag grows or shrinks; Staged only ever gets "the rest" (floored at
        // StagedMinHeight by the row's MinHeight), so it can't be driven off the bottom either.
        // The stage/unstage arrows sit in the Staged header's empty middle column so they line up
        // with the "Staged" label.
        var arrowStrip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0, // buttons sit flush against each other
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        arrowStrip.Children.Add(_stageArrow);
        arrowStrip.Children.Add(_unstageArrow);

        var changesLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _selectAllChanges.VerticalAlignment = VerticalAlignment.Center;
        _selectAllChanges.Margin = new Thickness(2, 0, 6, 0);
        _changesHeader.VerticalAlignment = VerticalAlignment.Center;
        changesLeft.Children.Add(_selectAllChanges);
        changesLeft.Children.Add(_changesHeader);

        var changesRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        changesRight.Children.Add(_discardAllChanges);
        var stageAllBtn = new Button
        {
            Content = "Stage all",
            FontSize = HeaderRowFontSize,
            Padding = new Thickness(8, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        stageAllBtn.Classes.Add("success");
        stageAllBtn.Click += (_, _) => _ = BulkAsync(_status.Unstaged.Concat(_status.Untracked).ToList(), staged: false);
        changesRight.Children.Add(stageAllBtn);

        var stagedLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _selectAllStaged.VerticalAlignment = VerticalAlignment.Center;
        _selectAllStaged.Margin = new Thickness(2, 0, 6, 0);
        _stagedHeader.VerticalAlignment = VerticalAlignment.Center;
        stagedLeft.Children.Add(_selectAllStaged);
        stagedLeft.Children.Add(_stagedHeader);

        var stagedRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var unstageAllBtn = new Button
        {
            Content = "Unstage all",
            FontSize = HeaderRowFontSize,
            Padding = new Thickness(8, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        unstageAllBtn.Classes.Add("danger");
        unstageAllBtn.Click += (_, _) => _ = BulkAsync(_status.Staged, staged: true);
        stagedRight.Children.Add(unstageAllBtn);

        var changesSection = SectionFrame.Build(
            left: changesLeft,
            center: null,
            right: changesRight,
            body: _changesRows,
            border: new Thickness(1, 1, 1, 0),
            minHeight: SectionFrame.TopMinHeight,
            out var _);

        var stagedSection = SectionFrame.Build(
            left: stagedLeft,
            center: arrowStrip,
            right: stagedRight,
            body: _stagedRows,
            border: new Thickness(1),
            minHeight: 96,
            out var stagedDividerHandle);

        var changesRow = new RowDefinition(new GridLength(SectionFrame.GetSplitHeight(_ctx.Id), GridUnitType.Pixel))
        {
            MinHeight = SectionFrame.TopMinHeight
        };

        var splitArea = new Grid
        {
            ClipToBounds = true, // a very short window clips the Staged list rather than letting it paint over the commit strip
            RowDefinitions =
            {
                changesRow,
                new RowDefinition(new GridLength(SectionFrame.HandleHeight, GridUnitType.Pixel)),
                new RowDefinition(new GridLength(1, GridUnitType.Star)) { MinHeight = SectionFrame.BottomMinHeight },
            },
        };
        Grid.SetRow(changesSection, 0);
        Grid.SetRow(stagedSection, 2);
        splitArea.Children.Add(changesSection);
        splitArea.Children.Add(stagedSection);

        // Thin grab strip sitting right on the Changes/Staged boundary.
        var splitHandle = new Border
        {
            Height = SectionFrame.HandleHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
        };
        Grid.SetRow(splitHandle, 1);
        splitArea.Children.Add(splitHandle);

        SectionFrame.AttachRowResize(splitHandle, splitArea, changesRow, h => SectionFrame.SetSplitHeight(_ctx.Id, h));
        SectionFrame.AttachRowResize(stagedDividerHandle, splitArea, changesRow, h => SectionFrame.SetSplitHeight(_ctx.Id, h));

        AttachedToVisualTree += (_, _) => SectionFrame.ApplySplitHeight(splitArea, changesRow, _ctx.Id);
        splitArea.SizeChanged += (_, _) => SectionFrame.ApplySplitHeight(splitArea, changesRow, _ctx.Id);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(messageSection, Dock.Bottom);
        root.Children.Add(messageSection);
        root.Children.Add(splitArea);
        Content = root;

        _status = ctx.State.Status;
        ctx.State.StatusChanged += OnStatusChanged;
        ctx.State.SelectedConflictPathChanged += OnSelectedConflictPathChanged;
        ctx.State.CommitMessageProposed += OnCommitMessageProposed;
        RenderLists();

        if (ctx.State.PendingCommitMessage is { } pending)
            ApplyProposal(pending);
    }

    // ── Commit messages proposed by an external tool ──────────────────────────────────────────

    private void BuildAgentProposalViews()
    {
        _offerText = Text("A coding agent proposed a commit message.", "ColorText", 12);
        _offerText.VerticalAlignment = VerticalAlignment.Center;
        _offerText.TextWrapping = TextWrapping.Wrap;
        _offerUse = TextButton("Use it", () => _ = UseOfferAsync());
        _offerDismiss = TextButton("Dismiss", DismissOffer);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(_offerUse);
        buttons.Children.Add(_offerDismiss);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(_offerText);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(buttons);

        _offerBar = new Border
        {
            Child = row,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(8, 4),
            IsVisible = false,
        }.Dyn(Border.BorderBrushProperty, "ColorAccent").Dyn(Border.BackgroundProperty, "ColorSurface2");

        _agentNote = Text("Message proposed by a coding agent. Review the staged files before committing.", "ColorTextMuted", 11);
        _agentNote.TextWrapping = TextWrapping.Wrap;
        _agentNote.IsVisible = false;
    }

    private void OnCommitMessageProposed(object? sender, CommitMessageProposedEventArgs e)
    {
        if (!_disposed)
            ApplyProposal(e.Message);
    }

    /// <summary>Puts the proposal in the box when that cannot lose anything — the box is empty or
    /// already holds the same text — and otherwise offers it in the bar above the box. A proposal
    /// never commits, never touches Amend and never moves focus.</summary>
    private void ApplyProposal(string proposal)
    {
        var current = _message.Text ?? "";
        var busy = _message.IsReadOnly || _committing;
        if (!busy && (string.IsNullOrWhiteSpace(current)
                      || string.Equals(current.Trim(), proposal.Trim(), StringComparison.Ordinal)))
        {
            TakeProposal(proposal);
            return;
        }

        _offered = proposal;
        _offerBar!.IsVisible = true;
        ToolTip.SetTip(_offerBar, proposal.Length <= 300 ? proposal : proposal[..300] + "…");
    }

    private void TakeProposal(string proposal)
    {
        _message.Text = proposal;
        _ctx.State.ClearPendingCommitMessage();
        HideOffer();
        _agentNoteText = _message.Text;
        _agentNote!.IsVisible = true;
    }

    private async Task UseOfferAsync()
    {
        if (_disposed || _offered is null || _message.IsReadOnly || _committing)
            return;

        var offer = _ctx.State.PendingCommitMessage ?? _offered;
        var draft = _message.Text ?? "";
        if (!string.IsNullOrWhiteSpace(draft)
            && !string.Equals(draft.Trim(), offer.Trim(), StringComparison.Ordinal)
            && !await ConfirmReplaceDraftAsync("the coding agent's message", draft.Length))
            return;

        if (_disposed || _message.IsReadOnly || _committing)
            return;

        TakeProposal(_ctx.State.PendingCommitMessage ?? offer);
    }

    private void DismissOffer()
    {
        _ctx.State.ClearPendingCommitMessage();
        HideOffer();
    }

    private void HideOffer()
    {
        _offered = null;
        if (_offerBar is not null)
        {
            _offerBar.IsVisible = false;
            ToolTip.SetTip(_offerBar, null);
        }
    }

    private void HideAgentNote()
    {
        _agentNoteText = null;
        if (_agentNote is not null)
            _agentNote.IsVisible = false;
    }

    /// <summary>Asks before a typed draft is thrown away for generated or proposed text.</summary>
    private Task<bool> ConfirmReplaceDraftAsync(string replacement, int draftLength) =>
        _dialogs.ConfirmDestructiveAsync("Replace commit message",
            $"Replace your commit message draft ({draftLength} characters) with {replacement}? Your draft is discarded.",
            "Replace");

    // The AI glyphs sit 2 px above the Amend label's size so they read at the same optical height.
    private const double CommitOptionsFontSize = 14;
    private const double AiGlyphSize = CommitOptionsFontSize + 2;
    private const double AiButtonSize = AiGlyphSize + 6;
    private const string WandTip = "Generate Commit Message";

    /// <summary>The wand (generate) and settings buttons at the left of the Amend row.
    /// The wand is green while an AI integration is active and grey, with its tooltip still
    /// shown, when none is.</summary>
    private Control BuildAiButtons()
    {
        _wand = new Button
        {
            Width = AiButtonSize,
            Height = AiButtonSize,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        ToolTip.SetShowOnDisabled(_wand, true);
        _wand.Click += (_, _) => GenerationForTesting = GenerateAsync();

        _aiSettings = new Button
        {
            Width = AiButtonSize,
            Height = AiButtonSize,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = IconGlyphPath("settings", AiGlyphSize, "ColorText"),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        ToolTip.SetTip(_aiSettings, "Setup AI Integration");
        _aiSettings.Click += (_, _) => _aiPanel!.Open(_aiProfiles!.ActiveProfile?.Id);

        _onAiChanged = (_, _) => Dispatcher.UIThread.Post(RefreshAiButtons);
        _aiProfiles!.Changed += _onAiChanged;
        RefreshAiButtons();

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _wand, _aiSettings },
        };
    }

    private void RefreshAiButtons()
    {
        if (_disposed || _wand is null || _aiProfiles is null)
            return;

        var enabled = _aiProfiles.ActiveProfile is not null;
        var generating = _generateCts is not null;
        _wand.IsEnabled = enabled || generating;
        _wand.Content = IconGlyphPath("wand_stars", AiGlyphSize, enabled ? "ColorSuccess" : "ColorTextFaint");
        _wand.Dyn(Button.BorderBrushProperty, enabled ? "ColorSuccess" : "ColorBorder");
        ToolTip.SetTip(_wand, generating ? "Generating… click to cancel" : WandTip);
    }

    /// <summary>Writes a commit message for the staged changes into the box. A second click while
    /// one is running cancels it. The box is read-only while the request runs, and the draft is
    /// replaced only after the request succeeds.</summary>
    private async Task GenerateAsync()
    {
        if (_generateCts is { } running)
        {
            running.Cancel();
            return;
        }
        var profile = _aiProfiles?.ActiveProfile;
        if (profile is null || _aiPanel is null || _aiGenerator is null)
            return;

        var amend = _amend.IsChecked == true;
        if (_status.Staged.Count == 0 && !amend)
        {
            _notifications.Publish(NotificationSeverity.Warning, "Stage changes before generating a commit message.");
            return;
        }

        var keyless = profile.Kind == AiServiceKind.Custom && !profile.ReadApiKeyFromEnv && string.IsNullOrWhiteSpace(profile.ApiKey);
        if (!_aiProfiles!.HasApiKey(profile) && !keyless)
        {
            _aiPanel.Open(profile.Id);
            _notifications.Publish(NotificationSeverity.Warning,
                $"{_aiProfiles.DisplayLabel(profile)} has no API key. Add one in AI Integrations.");
            return;
        }

        var draft = _message.Text ?? "";
        if (!string.IsNullOrWhiteSpace(draft)
            && !await ConfirmReplaceDraftAsync("an AI-generated message", draft.Length))
            return;
        if (_disposed || _generateCts is not null)
            return;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_ctx.Lifetime);
        _generateCts = cts;
        _message.IsReadOnly = true;
        RefreshAiButtons();
        try
        {
            var result = await _aiGenerator.GenerateAsync(_ctx, amend, cts.Token);
            if (_disposed || cts.IsCancellationRequested)
                return;

            _message.Text = result.Message;
            var label = _aiProfiles.DisplayLabel(_aiProfiles.ActiveProfile ?? profile);
            _notifications.Publish(NotificationSeverity.Info, $"Commit message generated by {label}.");
            if (result.ReplacedModel is not null && result.NewModel is not null)
                _notifications.Publish(NotificationSeverity.Warning,
                    $"'{result.ReplacedModel}' is no longer available. '{label}' now uses '{result.NewModel}'.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (AiException ex)
        {
            if (!_disposed)
                AiIntegrationsView.PublishError(_notifications, ex);
        }
        catch (Exception ex)
        {
            if (!_disposed)
                _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Generating the commit message"), ex.NotificationDetails());
        }
        finally
        {
            _generateCts = null;
            cts.Dispose();
            if (!_disposed)
            {
                _message.IsReadOnly = false;
                RefreshAiButtons();
            }
        }
    }

    private Button ArrowButton(string icon, string tintKey, string tip, Action onClick)
    {
        var glyph = IconGlyphPath(icon, 21, tintKey);
        var b = new Button
        {
            Content = glyph,
            Padding = new Thickness(6, 2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        b.Dyn(Button.BorderBrushProperty, "ColorBorder");
        ToolTip.SetTip(b, tip);
        b.Click += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    private RepositoryStatus? _lastRenderedStatus;
    private readonly Dictionary<string, (Border Row, CheckBox CheckBox, FileChangeKind Kind)> _changesRowCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Border Row, CheckBox CheckBox, FileChangeKind Kind)> _stagedRowCache = new(StringComparer.Ordinal);

    private void OnStatusChanged(object? s, StatusChangedEventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_lastRenderedStatus is not null && StatusEquivalent(_lastRenderedStatus, e.Status))
                return;
            _status = e.Status;
            RenderLists();
        });

    private static bool StatusEquivalent(RepositoryStatus a, RepositoryStatus b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Unstaged.Count != b.Unstaged.Count || a.Untracked.Count != b.Untracked.Count
            || a.Staged.Count != b.Staged.Count || a.Conflicted.Count != b.Conflicted.Count)
            return false;

        for (int i = 0; i < a.Unstaged.Count; i++)
            if (a.Unstaged[i].Path != b.Unstaged[i].Path || a.Unstaged[i].Kind != b.Unstaged[i].Kind) return false;
        for (int i = 0; i < a.Untracked.Count; i++)
            if (a.Untracked[i].Path != b.Untracked[i].Path || a.Untracked[i].Kind != b.Untracked[i].Kind) return false;
        for (int i = 0; i < a.Staged.Count; i++)
            if (a.Staged[i].Path != b.Staged[i].Path || a.Staged[i].Kind != b.Staged[i].Kind) return false;
        for (int i = 0; i < a.Conflicted.Count; i++)
            if (a.Conflicted[i].Path != b.Conflicted[i].Path || a.Conflicted[i].Kind != b.Conflicted[i].Kind) return false;

        return true;
    }

    private void OnSelectedConflictPathChanged(object? sender, SelectedConflictPathChangedEventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateConflictedRowHighlights);

    private void UpdateConflictedRowHighlights()
    {
        var selected = _ctx.State.SelectedConflictPath;
        foreach (var (path, border) in _conflictedRowCache)
        {
            if (selected != null && string.Equals(path, selected, StringComparison.Ordinal))
            {
                border.Dyn(Border.BackgroundProperty, "ColorListActive");
            }
            else
            {
                border.ClearValue(Border.BackgroundProperty);
                border.Background = Brushes.Transparent;
            }
        }
    }

    private void RenderLists()
    {
        var changes = _status.Unstaged.Concat(_status.Untracked).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var staged = _status.Staged.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var conflicted = _status.Conflicted.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

        // Drop checks for paths that no longer exist in the section.
        _checkedChanges.IntersectWith(changes.Select(f => f.Path));
        _checkedStaged.IntersectWith(staged.Select(f => f.Path));

        _changesHeader.Text = $"Changes ({changes.Count + conflicted.Count})";
        _stagedHeader.Text = $"Staged ({staged.Count})";

        // Conflicted rows cache prune
        var conflictedPaths = new HashSet<string>(conflicted.Select(f => f.Path), StringComparer.Ordinal);
        var removedConflicted = _conflictedRowCache.Keys.Where(k => !conflictedPaths.Contains(k)).ToList();
        foreach (var k in removedConflicted)
            _conflictedRowCache.Remove(k);

        var changesChildren = new List<Control>();

        if (conflicted.Count > 0)
        {
            var header = Text("Conflicted — resolve these before committing", "ColorDanger", 12, FontWeight.SemiBold);
            header.Margin = new Thickness(4, 2, 0, 2);
            changesChildren.Add(header);

            foreach (var f in conflicted)
            {
                var file = f;
                if (!_conflictedRowCache.TryGetValue(file.Path, out var border))
                {
                    var r = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Height = 24 };
                    var kind = KindLabel(FileChangeKind.Conflicted);
                    Grid.SetColumn(kind, 0);
                    r.Children.Add(kind);

                    var p = Text(file.Path, "ColorText", 12);
                    p.Margin = new Thickness(8, 0, 0, 0);
                    p.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(p, 1);
                    r.Children.Add(p);

                    border = new Border
                    {
                        Child = r,
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                        Background = Brushes.Transparent,
                    };

                    if (!_ctx.IsBare)
                    {
                        border.WithRevealMenu(_launcher, () => System.IO.Path.Combine(_ctx.RepositoryPath, file.Path));
                    }

                    border.PointerPressed += (_, _) =>
                    {
                        _ctx.State.SetOpenDiff(null);
                        _ctx.State.SetConflictResolverClosed(false);
                        _ctx.State.SetSelectedConflictPath(file.Path);
                    };

                    _conflictedRowCache[file.Path] = border;
                }

                changesChildren.Add(border);
            }
        }

        var normalChangeRows = BuildRows(changes, _checkedChanges, staged: false, _changesRowCache);
        changesChildren.AddRange(normalChangeRows);

        _changesRows.Children.Clear();
        if (changesChildren.Count == 0)
        {
            _changesRows.Children.Add(Text("No changes.", "ColorTextFaint", 12));
        }
        else
        {
            foreach (var c in changesChildren)
                _changesRows.Children.Add(c);
        }

        var stagedChildren = BuildRows(staged, _checkedStaged, staged: true, _stagedRowCache);
        _stagedRows.Children.Clear();
        if (stagedChildren.Count == 0)
        {
            _stagedRows.Children.Add(Text("Nothing staged.", "ColorTextFaint", 12));
        }
        else
        {
            foreach (var c in stagedChildren)
                _stagedRows.Children.Add(c);
        }

        UpdateConflictedRowHighlights();

        _lastRenderedStatus = _status;
        RefreshSelectionState();
    }

    private List<Control> BuildRows(
        IReadOnlyList<FileStatusEntry> files,
        HashSet<string> checkedSet,
        bool staged,
        Dictionary<string, (Border Row, CheckBox CheckBox, FileChangeKind Kind)> cache)
    {
        if (files.Count == 0)
        {
            cache.Clear();
            return [];
        }

        var currentPaths = new HashSet<string>(files.Select(f => f.Path), StringComparer.Ordinal);
        var removed = cache.Keys.Where(k => !currentPaths.Contains(k)).ToList();
        foreach (var k in removed)
            cache.Remove(k);

        var newChildren = new List<Control>(files.Count);
        foreach (var f in files)
        {
            var file = f;
            if (cache.TryGetValue(file.Path, out var cached) && cached.Kind == file.Kind)
            {
                cached.CheckBox.IsChecked = checkedSet.Contains(file.Path);
                newChildren.Add(cached.Row);
                continue;
            }

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Height = 24 };

            var cb = new CheckBox
            {
                IsChecked = checkedSet.Contains(file.Path),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 6, 0),
            };
            cb.IsCheckedChanged += (_, _) =>
            {
                if (cb.IsChecked == true) checkedSet.Add(file.Path);
                else checkedSet.Remove(file.Path);
                RefreshSelectionState();
            };
            row.Children.Add(cb);

            var kind = KindLabel(file.Kind);
            Grid.SetColumn(kind, 1);
            row.Children.Add(kind);

            var path = Text(file.Path, "ColorText", 12);
            path.Margin = new Thickness(8, 0, 0, 0);
            path.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(path, 2);
            row.Children.Add(path);

            if (!staged)
            {
                var discard = new Button { Content = "↺", Padding = new Thickness(4, 0), Background = Brushes.Transparent, BorderThickness = default };
                ToolTip.SetTip(discard, "Discard");
                discard.Click += (_, e) => { e.Handled = true; _ = DiscardAsync(file); };
                Grid.SetColumn(discard, 3);
                row.Children.Add(discard);
            }

            var b = new Border { Child = row, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), Background = Brushes.Transparent };
            if (!_ctx.IsBare)
            {
                if (!staged)
                {
                    b.WithRevealMenu(_launcher, () => System.IO.Path.Combine(_ctx.RepositoryPath, file.Path),
                        extraItems: [MenuLeaf("Discard changes", () => _ = DiscardAsync(file))]);
                }
                else
                {
                    b.WithRevealMenu(_launcher, () => System.IO.Path.Combine(_ctx.RepositoryPath, file.Path));
                }
            }
            b.PointerPressed += (_, e) =>
            {
                // e.Source is the leaf visual under the pointer — a Border/Path inside the
                // CheckBox template, not the CheckBox itself — so walk up before deciding.
                if (e.Source is Visual v && (v is CheckBox || v.FindAncestorOfType<CheckBox>() is not null))
                    return; // let the checkbox handle its own click
                _ctx.State.SetOpenDiff(new DiffRequest
                {
                    Source = staged ? DiffSource.StagedChanges : DiffSource.UnstagedChanges,
                    Path = file.Path,
                    ContextLines = _settings.Current.DiffContextLines,
                });
            };

            cache[file.Path] = (b, cb, file.Kind);
            newChildren.Add(b);
        }

        return newChildren;
    }

    private static Control KindLabel(FileChangeKind k)
    {
        var (label, key) = k switch
        {
            FileChangeKind.Added or FileChangeKind.Untracked => ("New", "ColorSuccess"),
            FileChangeKind.Deleted => ("Deleted", "ColorDanger"),
            FileChangeKind.Modified => ("Modified", "ColorWarning"),
            FileChangeKind.Renamed => ("Renamed", "ColorAccent"),
            FileChangeKind.Copied => ("Copied", "ColorAccent"),
            FileChangeKind.TypeChanged => ("Type-changed", "ColorWarning"),
            FileChangeKind.Conflicted => ("Conflict", "ColorDanger"),
            _ => ("Changed", "ColorTextMuted"),
        };
        var t = Text(label, key, 11, FontWeight.SemiBold);
        t.Width = 84;
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }

    // ── selection + stage/unstage arrows ──

    private void RefreshSelectionState()
    {
        var changesCount = _status.Unstaged.Count + _status.Untracked.Count;
        var stagedCount = _status.Staged.Count;

        _stageArrow.IsEnabled = _checkedChanges.Count > 0;
        _unstageArrow.IsEnabled = _checkedStaged.Count > 0;
        _stashStaged.IsEnabled = !_stashing && stagedCount > 0;
        _discardAllChanges.IsEnabled = changesCount > 0;

        _suppressSelectAll = true;
        _selectAllChanges.IsChecked = TriState(_checkedChanges.Count, changesCount);
        _selectAllStaged.IsChecked = TriState(_checkedStaged.Count, stagedCount);
        _suppressSelectAll = false;
    }

    private static bool? TriState(int chosen, int total)
        => total == 0 || chosen == 0 ? false : chosen >= total ? true : null;

    private void ToggleSelectAll(bool staged)
    {
        if (_suppressSelectAll) return;
        var set = staged ? _checkedStaged : _checkedChanges;
        var all = staged
            ? _status.Staged.Select(f => f.Path)
            : _status.Unstaged.Concat(_status.Untracked).Select(f => f.Path);
        var want = (staged ? _selectAllStaged : _selectAllChanges).IsChecked == true;
        set.Clear();
        if (want)
            foreach (var p in all) set.Add(p);
        RenderLists();
    }

    private async Task MoveCheckedAsync(bool stage)
    {
        var checkedPaths = (stage ? _checkedChanges : _checkedStaged).ToList();
        if (checkedPaths.Count == 0) return;
        (stage ? _checkedChanges : _checkedStaged).Clear();

        // A checked rename must move both halves together — staging (or unstaging) only the
        // new path leaves the old path's deletion behind as a separate, unstaged entry instead of
        // one Renamed entry. Expand at send time only: the checked sets themselves stay keyed on
        // visible-row paths (see RefreshSelectionState's tri-state maths).
        var pool = stage
            ? (IReadOnlyList<FileStatusEntry>)[.. _status.Unstaged, .. _status.Untracked]
            : _status.Staged;
        var paths = StagePathExpansion.ForStaging(pool, checkedPaths);

        await Run(() => stage
            ? _ctx.Gateway.StageAsync(paths, _ctx.Lifetime)
            : _ctx.Gateway.UnstageAsync(paths, _ctx.Lifetime));
    }

    private async Task DiscardAsync(FileStatusEntry f)
    {
        if (!await _dialogs.ConfirmDestructiveAsync(
            "Discard changes",
            $"Discard unstaged changes to '{f.Path}'? Staged changes are kept. This cannot be undone.",
            "Discard"))
            return;
        _checkedChanges.Remove(f.Path);
        _checkedStaged.Remove(f.Path);
        if (f.OldPath is not null)
        {
            _checkedChanges.Remove(f.OldPath);
            _checkedStaged.Remove(f.OldPath);
        }
        RefreshSelectionState();
        if (_ctx.State.OpenDiff?.Path == f.Path || (f.OldPath is not null && _ctx.State.OpenDiff?.Path == f.OldPath))
            _ctx.State.SetOpenDiff(null);

        var paths = f.OldPath is not null ? new[] { f.Path, f.OldPath } : new[] { f.Path };
        await Run(() => _ctx.Gateway.DiscardAsync(paths, _ctx.Lifetime));
    }

    private async Task DiscardAllChangesAsync()
    {
        var files = _status.Unstaged.Concat(_status.Untracked);
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            if (seen.Add(f.Path))
                paths.Add(f.Path);
            if (f.OldPath is not null && seen.Add(f.OldPath))
                paths.Add(f.OldPath);
        }

        if (paths.Count == 0) return;
        if (!await _dialogs.ConfirmDestructiveAsync(
            "Discard all changes",
            "Discard all unstaged changes? Untracked files are moved to the recycle bin; "
            + "changes to tracked files cannot be undone.",
            "Discard all"))
            return;
        _checkedChanges.Clear();
        RefreshSelectionState();
        if (_ctx.State.OpenDiff is { } diff && (diff.Source == DiffSource.UnstagedChanges || (diff.Path is not null && seen.Contains(diff.Path))))
            _ctx.State.SetOpenDiff(null);
        await Run(() => _ctx.Gateway.DiscardAsync(paths, _ctx.Lifetime));
    }

    private async Task BulkAsync(IReadOnlyList<FileStatusEntry> files, bool staged)
    {
        if (files.Count == 0) return;
        // Same expansion as MoveCheckedAsync — a bulk stage/unstage must move both halves of
        // any rename in the list, not just the new path.
        var paths = StagePathExpansion.ForStaging(files, files.Select(f => f.Path));
        await Run(() => staged ? _ctx.Gateway.UnstageAsync(paths, _ctx.Lifetime) : _ctx.Gateway.StageAsync(paths, _ctx.Lifetime));
    }

    private async Task Run(Func<Task> op)
    {
        try { await op(); await _ctx.State.RefreshStatusAsync(_ctx.Lifetime); }
        catch (Exception ex) { _notifications.Publish(NotificationSeverity.Error, ex.Message, ex.NotificationDetails()); }
    }

    // Stash only the staged set; unstaged working-tree edits are left in place.
    private async Task StashStagedAsync()
    {
        if (_stashing || _status.Staged.Count == 0)
            return;
        _stashing = true;
        _stashStaged.IsEnabled = false;
        try
        {
            var stashed = await _stash.StashStagedAsync(_ctx, null, _ctx.Lifetime);
            _notifications.Publish(
                stashed ? NotificationSeverity.Success : NotificationSeverity.Info,
                stashed ? "Stashed the staged changes." : "Nothing staged to stash.");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Stash staged"), ex.NotificationDetails());
        }
        finally
        {
            _stashing = false;
            RefreshSelectionState();
        }
    }

    private async Task CommitAsync(bool push)
    {
        // While a generated message is on its way, Ctrl+Enter must not commit the old draft just
        // before the generated text replaces it.
        if (_committing || _generateCts is not null || _status.Conflicted.Count > 0) return;
        if (string.IsNullOrWhiteSpace(_message.Text) || (_status.Staged.Count == 0 && _amend.IsChecked != true))
        {
            _notifications.Publish(NotificationSeverity.Warning, "Stage changes and enter a message before committing.");
            return;
        }
        // Amending a commit that's already on its upstream (as of the last
        // fetch) rewrites published history — pushing it afterwards needs a force push. An
        // unpushed commit's amend doesn't touch anything else and needs no prompt.
        if (_amend.IsChecked == true)
        {
            var upstream = _ctx.State.CurrentBranch?.UpstreamName;
            if (upstream is not null && _status.AheadBy == 0)
            {
                var headSha = _ctx.State.CurrentBranch?.TipCommitId?.ToString();
                var shortSha = headSha is { Length: >= 7 } ? headSha[..7] : headSha ?? "HEAD";
                if (!await _dialogs.ConfirmDestructiveAsync("Amend a pushed commit",
                        $"Amend {shortSha}, which is already on {upstream}? This rewrites " +
                        "published history; pushing it afterwards needs a force push.", "Amend"))
                    return;
            }
        }

        _committing = true;
        // A proposal that arrives while the commit runs is meant for the next commit; only the
        // one already pending now is spent by this commit.
        var pendingAtStart = _ctx.State.PendingCommitMessage;
        try
        {
            var profile = _ctx.State.ActiveProfile;
            var name = profile?.GitUserName ?? "";
            var email = profile?.GitUserEmail ?? "";

            // under Local config (no active profile) the identity comes from git config.
            if (profile is null && (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email)))
            {
                var (cfgName, cfgEmail) = await _ctx.Gateway.GetConfiguredIdentityAsync(_ctx.Lifetime);
                if (string.IsNullOrWhiteSpace(cfgName) || string.IsNullOrWhiteSpace(cfgEmail))
                    (cfgName, cfgEmail) = await _ctx.Gateway.GetGlobalIdentityAsync(_ctx.Lifetime);
                name = cfgName ?? name;
                email = cfgEmail ?? email;
            }

            if (TryParseAuthor(_author.Text ?? "", out var oN, out var oE)) { name = oN; email = oE; }
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
            {
                _notifications.Publish(NotificationSeverity.Warning,
                    "No author identity. Set user.name / user.email in Git config, choose a profile, or set an author override.");
                return;
            }

            // with no profile, honour whatever git config says about signing.
            var sign = profile?.SignCommits ?? await _ctx.Gateway.GetGpgSignConfiguredAsync(_ctx.Lifetime);

            await _ctx.Gateway.CommitAsync(new CommitRequest
            {
                Message = _message.Text!.Trim(),
                AuthorName = name, AuthorEmail = email,
                CommitterName = profile?.GitUserName ?? name, CommitterEmail = profile?.GitUserEmail ?? email,
                AmendPreviousCommit = _amend.IsChecked == true,
                Sign = sign,
            }, _ctx.Lifetime);
            _message.Text = "";
            _amend.IsChecked = false;
            DraftByRepo.Remove(_ctx.Id);
            if (pendingAtStart is not null)
                _ctx.State.ClearPendingCommitMessage(onlyIf: pendingAtStart);
            if (_ctx.State.PendingCommitMessage is null)
                HideOffer();
            HideAgentNote();
            await _ctx.State.RefreshAllAsync(_ctx.Lifetime);
            _notifications.Publish(NotificationSeverity.Success, "Commit created.");

            // The push goes through the network-action runner, which owns its progress,
            // cancellation, busy guard and rejection / host-key / Local-config flows, and
            // reports its own outcome.
            if (push && BuildPushRequest(_ctx) is { } request)
                _actionBus.RequestNetworkAction(request);
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Commit"), ex.NotificationDetails());
        }
        finally
        {
            _committing = false;
            if (!_disposed && _ctx.State.PendingCommitMessage is { } pending)
                ApplyProposal(pending);
        }
    }

    /// <summary>The push half of Commit &amp; Push: the current branch to <c>origin</c> (else the
    /// first remote), pinned to this panel's repository. Null when there is no remote or no
    /// checked-out branch.</summary>
    internal static RepoNetworkRequest? BuildPushRequest(RepositoryContext ctx)
    {
        var remote = ctx.State.Remotes.FirstOrDefault(r => r.Name == "origin")?.Name ?? ctx.State.Remotes.FirstOrDefault()?.Name;
        var branch = ctx.State.CurrentBranch?.Name;
        return remote is null || branch is null
            ? null
            : new RepoNetworkRequest(RepoNetworkAction.Push, remote, branch, Repo: ctx);
    }

    private void UpdateCounter()
    {
        var len = (_message.Text ?? "").Length;
        string secondPart;
        string colorKey;

        if (len < 72)
        {
            secondPart = "72";
            colorKey = "ColorTextMuted";
        }
        else if (len == 72)
        {
            secondPart = "72";
            colorKey = "ColorWarning";
        }
        else if (len < 500)
        {
            secondPart = "500";
            colorKey = "ColorWarning";
        }
        else
        {
            secondPart = "-";
            colorKey = "ColorDanger";
        }

        _counter.Text = $"{len} / {secondPart}";
        _counter.Dyn(TextBlock.ForegroundProperty, colorKey);
    }

    private static bool TryParseAuthor(string text, out string name, out string email)
    {
        name = ""; email = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lt = text.IndexOf('<');
        var gt = text.IndexOf('>');
        if (lt < 0 || gt < lt) return false;
        name = text[..lt].Trim();
        email = text[(lt + 1)..gt].Trim();
        return name.Length > 0 && email.Length > 0;
    }

    public void Dispose()
    {
        _disposed = true;
        // Cancelled before the draft is saved: the generated text would otherwise land in a dead
        // view while the draft cache kept the old text.
        _generateCts?.Cancel();
        if (_aiProfiles is not null && _onAiChanged is not null)
            _aiProfiles.Changed -= _onAiChanged;
        _ctx.State.StatusChanged -= OnStatusChanged;
        _ctx.State.SelectedConflictPathChanged -= OnSelectedConflictPathChanged;
        _ctx.State.CommitMessageProposed -= OnCommitMessageProposed;
        _commitShortcut?.Dispose();
        var msg = _message.Text ?? "";
        if (msg.Length > 0) DraftByRepo[_ctx.Id] = msg;
        else DraftByRepo.Remove(_ctx.Id);
    }
}
