namespace Historia.Controls;

/// <summary>
/// The custom-drawn embedded terminal (V5 §6). A monospace grid of coloured cells — a simpler
/// drawing problem than <see cref="CommitGraphControl"/>: no lane assignment, no DAG layout, no
/// bezier routing. Windowed render, pooled brushes/pens, a per-codepoint glyph cache, all
/// miss-counted through the Core <see cref="RenderResourceCache{TKey,TValue}"/> (rule 24).
/// </summary>
public sealed class TerminalControl : Control
{
    private const double DefaultFontSize = 13;
    private const int MinGrid = 1;
    private const int MaxGrid = 1000;

    private IThemeService _theme = default!;
    private IUiDispatcher _ui = default!;
    private ISettingsService _settings = default!;
    private IExternalLauncher _launcher = default!;
    private bool _wired;

    private TerminalSession? _session;
    private EffectiveTheme _themeMode = EffectiveTheme.Dark;

    private Typeface _typeface = new(FontFamily.Default);
    private IGlyphTypeface? _glyphTypeface;
    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private double _lastScaling = 1;
    private string? _lastFontFamily;
    private double _lastFontSize = DefaultFontSize;

    private int _cols = 80;
    private int _rows = 24;
    private double _scrollOffsetLines;               // 0 = bottom (follow); >0 = scrolled back
    private DispatcherTimer? _resizeDebounce;

    // selection (viewport-absolute line + column)
    private bool _selecting;
    private (int Line, int Col)? _selAnchor;
    private (int Line, int Col)? _selHead;

    private IUiTimer? _blinkTimer;
    private bool _blinkOn = true;

    private readonly RenderResourceCache<uint, IBrush> _brushes;
    private readonly RenderResourceCache<PenKey, IPen> _pens;
    private readonly RenderResourceCache<int, GlyphMetric> _glyphs;

    private readonly record struct PenKey(uint Argb, double Thickness, int Style);
    private readonly record struct GlyphMetric(ushort Index, double Advance, bool Found);

    public TerminalControl()
    {
        Focusable = true;
        ClipToBounds = true;

        _brushes = new RenderResourceCache<uint, IBrush>(
            1024, argb => new ImmutableSolidColorBrush(AvColor.FromUInt32(argb)));
        _pens = new RenderResourceCache<PenKey, IPen>(
            256, k => new ImmutablePen(new ImmutableSolidColorBrush(AvColor.FromUInt32(k.Argb)), k.Thickness));
        _glyphs = new RenderResourceCache<int, GlyphMetric>(4096, ResolveGlyph);
    }

    // ── lifecycle ───────────────────────────────────────────────────────────────────────────

    public void Attach(IServiceProvider services)
    {
        if (_wired)
            return;
        _theme = services.GetRequiredService<IThemeService>();
        _ui = services.GetRequiredService<IUiDispatcher>();
        _settings = services.GetRequiredService<ISettingsService>();
        _launcher = services.GetRequiredService<IExternalLauncher>();
        _themeMode = _theme.Effective;
        _theme.ThemeChanged += OnThemeChanged;
        _settings.Changed += OnSettingsChanged;
        _wired = true;
        RemeasureFont();
    }

    public TerminalSession? Session => _session;

    /// <summary>Raised when the user presses Enter in a session whose shell has exited — the
    /// dock restarts it (V5 §7.2, "no auto-respawn").</summary>
    public event EventHandler? RestartRequested;

    public void SetSession(TerminalSession? session)
    {
        if (ReferenceEquals(_session, session))
            return;

        if (_session is not null)
            _session.BufferChanged -= OnSessionBufferChanged;

        _session = session;
        _scrollOffsetLines = 0;
        _selAnchor = _selHead = null;

        if (_session is not null)
        {
            _session.BufferChanged += OnSessionBufferChanged;
            PushResizeToSession();
        }

        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _blinkTimer ??= _ui?.CreateTimer(TimeSpan.FromMilliseconds(530), OnBlinkTick);
        _blinkTimer?.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _blinkTimer?.Stop();
    }

    public void Teardown()
    {
        if (_session is not null)
            _session.BufferChanged -= OnSessionBufferChanged;
        if (_wired)
        {
            _theme.ThemeChanged -= OnThemeChanged;
            _settings.Changed -= OnSettingsChanged;
        }
        _blinkTimer?.Dispose();
        _blinkTimer = null;
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e)
    {
        _themeMode = e.NewTheme;
        _brushes.Clear();
        _pens.Clear();
        InvalidateVisual();
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.OldSettings.TerminalFontFamily != e.NewSettings.TerminalFontFamily
            || e.OldSettings.TerminalFontSize != e.NewSettings.TerminalFontSize)
        {
            RemeasureFont();
            RecomputeGrid();
            PushResizeToSession();
            InvalidateVisual();
        }
    }

    private void OnSessionBufferChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);

    private void OnBlinkTick()
    {
        var blink = _settings?.Current.TerminalCursorBlink ?? true;
        if (!blink)
        {
            if (!_blinkOn) { _blinkOn = true; InvalidateVisual(); }
            return;
        }
        _blinkOn = !_blinkOn;
        InvalidateVisual();
    }

    // ── font metrics (V5 §6.1) ──────────────────────────────────────────────────────────────

    private void RemeasureFont()
    {
        var family = _settings?.Current.TerminalFontFamily;
        var size = _settings?.Current.TerminalFontSize ?? DefaultFontSize;
        if (size < 6) size = DefaultFontSize;

        _lastFontFamily = family;
        _lastFontSize = size;
        _lastScaling = (this.GetVisualRoot() as ILayoutRoot)?.LayoutScaling ?? 1;

        var ff = string.IsNullOrWhiteSpace(family)
            ? new FontFamily(PlatformMonospaceStack())
            : new FontFamily(family);
        _typeface = new Typeface(ff);

        _glyphTypeface = null;
        try
        {
            if (FontManager.Current.TryGetGlyphTypeface(_typeface, out var gtf))
                _glyphTypeface = gtf;
        }
        catch { _glyphTypeface = null; }

        // Advance width of 'M', line height from the typeface metrics.
        var probe = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typeface, size, Brushes.White);
        _cellWidth = Math.Max(1, Math.Round(probe.WidthIncludingTrailingWhitespace));
        _cellHeight = Math.Max(1, Math.Ceiling(probe.Height));

        _glyphs.Clear();
    }

    private static string PlatformMonospaceStack()
    {
        if (OperatingSystem.IsWindows()) return "Cascadia Mono, Consolas, Courier New";
        if (OperatingSystem.IsMacOS()) return "SF Mono, Menlo, Monaco";
        return "DejaVu Sans Mono, Liberation Mono, monospace";
    }

    private GlyphMetric ResolveGlyph(int codepoint)
    {
        var gtf = _glyphTypeface;
        if (gtf is null)
            return new GlyphMetric(0, _cellWidth, false);
        try
        {
            if (gtf.TryGetGlyph((uint)codepoint, out var gi))
            {
                var em = gtf.Metrics.DesignEmHeight == 0 ? 1000 : gtf.Metrics.DesignEmHeight;
                var adv = gtf.GetGlyphAdvance(gi) * (_lastFontSize / em);
                return new GlyphMetric(gi, adv, true);
            }
        }
        catch { /* fall through */ }
        return new GlyphMetric(0, _cellWidth, false);
    }

    // ── sizing ──────────────────────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize) => availableSize;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        RecomputeGrid();
        DebouncePushResize();
    }

    private void RecomputeGrid()
    {
        if (_cellWidth <= 0 || _cellHeight <= 0)
            return;
        _cols = Math.Clamp((int)(Bounds.Width / _cellWidth), MinGrid, MaxGrid);
        _rows = Math.Clamp((int)(Bounds.Height / _cellHeight), MinGrid, MaxGrid);
    }

    private void DebouncePushResize()
    {
        _resizeDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _resizeDebounce.Stop();
        _resizeDebounce.Tick -= OnResizeDebounceTick;
        _resizeDebounce.Tick += OnResizeDebounceTick;
        _resizeDebounce.Start();
    }

    private void OnResizeDebounceTick(object? sender, EventArgs e)
    {
        _resizeDebounce!.Stop();
        PushResizeToSession();
    }

    private void PushResizeToSession()
    {
        RecomputeGrid();
        _session?.Resize(_cols, _rows);
        InvalidateVisual();
    }

    // ── render (V5 §6.2) ────────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext dc)
    {
        var palette = HistoriaPalette.For(_themeMode);
        var termBg = Brush(ArgbOf(palette["term-bg"]));
        dc.FillRectangle(termBg, new Rect(Bounds.Size));

        if (_session is null)
            return;

        var (cols, rows) = (_cols, _rows);
        var (metMax, metValue) = (0, 0);

        _session.ReadBuffer(view =>
        {
            var total = view.TotalLines;
            // The emulator's own viewport top (YDisp) is the authoritative bottom-anchor:
            // XTerm.NET pre-grows the buffer toward its scrollback capacity, so `total - rows`
            // points at blank pre-allocated lines far below the content (review §6).
            var bottomTop = Math.Clamp(view.ViewportTop, 0, Math.Max(0, total - 1));
            var top = Math.Clamp(bottomTop - (int)Math.Round(_scrollOffsetLines), 0, bottomTop);
            (metMax, metValue) = (bottomTop, top);

            for (var screenRow = 0; screenRow < rows; screenRow++)
            {
                var line = top + screenRow;
                if (line >= total)
                    break;
                DrawRow(dc, view, line, screenRow, cols, palette);
            }

            DrawCursor(dc, view, top, rows, cols, palette);
            return 0;
        });

        var metrics = new ScrollMetrics(metMax, metValue, rows);
        if (metrics != _lastMetrics)
        {
            _lastMetrics = metrics;
            Dispatcher.UIThread.Post(() => ScrollMetricsChanged?.Invoke(this, EventArgs.Empty));
        }
    }

    // ── scroll metrics (drives the dock's vertical ScrollBar) ────────────────────────────────

    /// <summary>Scrollback geometry in line units: <paramref name="Max"/> lines are hidden above
    /// the viewport at the top-most scroll, <paramref name="Value"/> is how many are hidden right
    /// now (0 = following the bottom), <paramref name="Viewport"/> is the visible row count.</summary>
    public readonly record struct ScrollMetrics(int Max, int Value, int Viewport);

    private ScrollMetrics _lastMetrics = new(-1, -1, -1);

    /// <summary>Latest metrics from the last render — no buffer lock needed.</summary>
    public ScrollMetrics CurrentScrollMetrics => _lastMetrics;

    /// <summary>Raised (on the dispatcher) after a render whose scroll metrics changed.</summary>
    public event EventHandler? ScrollMetricsChanged;

    /// <summary>Scroll so <paramref name="value"/> lines are hidden above the viewport
    /// (<c>value == Max</c> → top, <c>0</c> → follow the bottom). Driven by the dock ScrollBar.</summary>
    public void SetScrollValue(int value)
    {
        var max = _lastMetrics.Max;
        if (max <= 0)
            return;
        _scrollOffsetLines = Math.Clamp(max - value, 0, max);
        InvalidateVisual();
    }

    private void DrawRow(DrawingContext dc, ITerminalBufferView view, int line, int screenRow, int cols,
        IReadOnlyDictionary<string, Rgb> palette)
    {
        var y = screenRow * _cellHeight;
        var defaultBg = ArgbOf(palette["term-bg"]);

        // Pass A — backgrounds: merge adjacent equal-bg cells into one rect.
        var runStart = 0;
        uint runBg = 0;
        var haveRun = false;
        for (var col = 0; col <= cols; col++)
        {
            uint bg = col < cols ? ResolveBg(view.GetCell(line, col), palette) : defaultBg + 1; // sentinel to flush
            if (haveRun && bg == runBg)
                continue;
            if (haveRun && runBg != defaultBg)
            {
                dc.FillRectangle(Brush(runBg),
                    new Rect(runStart * _cellWidth, y, (col - runStart) * _cellWidth, _cellHeight));
            }
            runStart = col;
            runBg = bg;
            haveRun = true;
        }

        // Pass B — text runs: merge adjacent equal (fg, flags).
        var b = 0;
        while (b < cols)
        {
            var cell = view.GetCell(line, b);
            if (cell.Width == 0 || cell.CodePoint == 0 || cell.CodePoint == 32)
            {
                b++;
                continue;
            }

            var (fg, flags) = ResolveFg(cell, palette);
            var start = b;
            var sb = new System.Text.StringBuilder();
            while (b < cols)
            {
                var c = view.GetCell(line, b);
                if (c.Width == 0) { b++; continue; }
                if (c.CodePoint is 0 or 32) break;
                var (cfg, cflags) = ResolveFg(c, palette);
                if (cfg != fg || cflags != flags) break;
                sb.Append(c.Content.Length > 0 ? c.Content : char.ConvertFromUtf32(c.CodePoint));
                b += Math.Max(1, c.Width);
            }
            if (b == start) { b++; continue; }

            DrawTextRun(dc, sb.ToString(), start, y, fg, flags);
        }
    }

    private void DrawTextRun(DrawingContext dc, string text, int startCol, double y, uint fgArgb, TerminalCellFlags flags)
    {
        var brush = Brush(fgArgb);
        var origin = new Point(startCol * _cellWidth, y);

        var gtf = _glyphTypeface;
        var runes = text.EnumerateRunes().ToArray();
        var allFound = gtf is not null;
        var indices = new ushort[runes.Length];
        for (var i = 0; i < runes.Length && allFound; i++)
        {
            var m = _glyphs.Get(runes[i].Value);
            if (!m.Found) { allFound = false; break; }
            indices[i] = m.Index;
        }

        if (allFound && gtf is not null)
        {
            var em = gtf.Metrics.DesignEmHeight == 0 ? 1000 : gtf.Metrics.DesignEmHeight;
            var ascentPx = -gtf.Metrics.Ascent * (_lastFontSize / em);
            var baseline = y + ascentPx;
            var glyphRun = new GlyphRun(gtf, _lastFontSize, text.AsMemory(), indices,
                baselineOrigin: new Point(startCol * _cellWidth, baseline));
            dc.DrawGlyphRun(brush, glyphRun);
        }
        else
        {
            // Fallback path (missing glyph / no glyph typeface): FormattedText, not cached.
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                Face(flags), _lastFontSize, brush);
            dc.DrawText(ft, origin);
        }

        DrawTextDecorations(dc, text.Length, startCol, y, fgArgb, flags);
    }

    private void DrawTextDecorations(DrawingContext dc, int cellCount, int startCol, double y, uint fgArgb, TerminalCellFlags flags)
    {
        var x0 = startCol * _cellWidth;
        var x1 = x0 + cellCount * _cellWidth;
        if (flags.HasFlag(TerminalCellFlags.Underline))
        {
            var uy = y + _cellHeight - 1.5;
            dc.DrawLine(Pen(fgArgb, 1, 0), new Point(x0, uy), new Point(x1, uy));
        }
        if (flags.HasFlag(TerminalCellFlags.Strikethrough))
        {
            var sy = y + _cellHeight / 2;
            dc.DrawLine(Pen(fgArgb, 1, 1), new Point(x0, sy), new Point(x1, sy));
        }
    }

    private void DrawCursor(DrawingContext dc, ITerminalBufferView view, int top, int rows, int cols,
        IReadOnlyDictionary<string, Rgb> palette)
    {
        if (!view.CursorVisible)
            return;
        var screenRow = view.CursorRow - top;
        if (screenRow < 0 || screenRow >= rows)
            return;
        var col = Math.Clamp(view.CursorColumn, 0, cols - 1);
        var rect = new Rect(col * _cellWidth, screenRow * _cellHeight, _cellWidth, _cellHeight);
        var cursorBrush = Brush(ArgbOf(palette["term-cursor"]));

        var style = _settings?.Current.TerminalCursorStyle ?? TerminalCursorStyle.Block;
        var focused = IsFocused;

        if (!focused)
        {
            dc.DrawRectangle(null, new ImmutablePen(new ImmutableSolidColorBrush(AvColor.FromUInt32(ArgbOf(palette["term-cursor"]))), 1), rect);
            return;
        }
        if (!_blinkOn)
            return;

        switch (style)
        {
            case TerminalCursorStyle.Underline:
                dc.FillRectangle(cursorBrush, new Rect(rect.X, rect.Bottom - 2, rect.Width, 2));
                break;
            case TerminalCursorStyle.Bar:
                dc.FillRectangle(cursorBrush, new Rect(rect.X, rect.Y, 2, rect.Height));
                break;
            default:
                dc.FillRectangle(cursorBrush, rect);
                break;
        }
    }

    // ── colour resolution ───────────────────────────────────────────────────────────────────

    private uint ResolveBg(TerminalCellSnapshot cell, IReadOnlyDictionary<string, Rgb> palette)
    {
        var (bg, _) = ApplyInverse(cell, palette);
        return bg;
    }

    private (uint Fg, TerminalCellFlags Flags) ResolveFg(TerminalCellSnapshot cell, IReadOnlyDictionary<string, Rgb> palette)
    {
        var (_, fg) = ApplyInverse(cell, palette);
        var flags = cell.Flags;
        if (flags.HasFlag(TerminalCellFlags.Hidden))
            fg = ResolveBg(cell, palette);
        if (flags.HasFlag(TerminalCellFlags.Dim))
            fg = Blend(fg, ResolveBg(cell, palette), 0.4);
        return (fg, flags);
    }

    /// <summary>Resolves fg/bg, swapping them first when the cell is inverse (V5 §6.2) so
    /// inverse cells still merge into runs.</summary>
    private (uint Bg, uint Fg) ApplyInverse(TerminalCellSnapshot cell, IReadOnlyDictionary<string, Rgb> palette)
    {
        var fg = ArgbOf(TerminalColorResolver.Resolve(cell.Foreground, _themeMode));
        var bg = ArgbOf(TerminalColorResolver.Resolve(cell.Background, _themeMode));
        return cell.Flags.HasFlag(TerminalCellFlags.Inverse) ? (fg, bg) : (bg, fg);
    }

    private Typeface Face(TerminalCellFlags flags)
    {
        var weight = flags.HasFlag(TerminalCellFlags.Bold) ? FontWeight.Bold : FontWeight.Normal;
        var style = flags.HasFlag(TerminalCellFlags.Italic) ? FontStyle.Italic : FontStyle.Normal;
        return new Typeface(_typeface.FontFamily, style, weight);
    }

    // ── input (V5 §6.5) ─────────────────────────────────────────────────────────────────────

    protected override void OnGotFocus(Avalonia.Input.GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_session is null || string.IsNullOrEmpty(e.Text))
            return;
        _session.Write(System.Text.Encoding.UTF8.GetBytes(e.Text));
        _scrollOffsetLines = 0;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_session is null)
        {
            base.OnKeyDown(e);
            return;
        }

        if (_session.State is TerminalSessionState.Exited)
        {
            if (e.Key is Key.Enter or Key.Return)
            {
                RestartRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
            return;
        }

        var mods = ToModifiers(e.KeyModifiers);

        // Terminal-scoped chords the control does NOT claim (V5 §7.3).
        if (mods == TerminalModifiers.Control && e.Key == Key.OemTilde) { base.OnKeyDown(e); return; }
        if (mods.HasFlag(TerminalModifiers.Control) && mods.HasFlag(TerminalModifiers.Shift)
            && e.Key is Key.C or Key.V or Key.F)
        {
            if (e.Key == Key.C) { CopySelection(); e.Handled = true; }
            else if (e.Key == Key.V) { _ = PasteAsync(); e.Handled = true; }
            return;
        }

        var modes = ReadModes();

        if (TryMapKey(e.Key, out var tkey))
        {
            var bytes = KeyEncoder.Encode(tkey, mods, modes);
            if (!bytes.IsEmpty)
            {
                _session.Write(bytes);
                _scrollOffsetLines = 0;
                e.Handled = true;
                return;
            }
        }

        // Ctrl+letter / Ctrl+punctuation control codes.
        if (mods.HasFlag(TerminalModifiers.Control) && !mods.HasFlag(TerminalModifiers.Alt))
        {
            var ch = ControlCharFor(e.Key);
            if (ch is not null && KeyEncoder.EncodeControlChar(ch.Value, TerminalModifiers.Control) is { } code)
            {
                _session.Write(new[] { code });
                _scrollOffsetLines = 0;
                e.Handled = true;
                return;
            }
        }

        // Alt+letter / Alt+digit / Alt+punctuation (xterm metaSendsEscape).
        if (mods.HasFlag(TerminalModifiers.Alt) && !mods.HasFlag(TerminalModifiers.Control))
        {
            var ch = MetaCharFor(e.Key, mods.HasFlag(TerminalModifiers.Shift));
            if (ch is not null && KeyEncoder.EncodeMeta(ch.Value, mods) is { } metaBytes)
            {
                _session.Write(metaBytes);
                _scrollOffsetLines = 0;
                e.Handled = true;
                return;
            }
        }

        // Only keys that cannot produce text (exactly one of Ctrl or Alt held) are claimed
        // here; plain keys, Shift-only keys, and Ctrl+Alt (AltGr) must stay unhandled so the
        // platform can deliver character messages to OnTextInput.
        if (e.Key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin))
        {
            if (KeyEncoder.ClaimsUnencodedKey(mods))
                e.Handled = true;
        }
    }

    private TerminalModes ReadModes() => _session?.CurrentModes ?? default;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_session is null)
            return;

        var isAlt = _session.ReadBuffer(v => v.IsAlternateBuffer);
        if (isAlt)
        {
            var key = e.Delta.Y > 0 ? TerminalKey.Up : TerminalKey.Down;
            for (var i = 0; i < 3; i++)
                _session.Write(KeyEncoder.Encode(key, TerminalModifiers.None, ReadModes()));
        }
        else
        {
            _scrollOffsetLines = Math.Max(0, _scrollOffsetLines + (e.Delta.Y > 0 ? 3 : -3));
            InvalidateVisual();
        }
        e.Handled = true;
    }

    // ── selection & clipboard (V5 §6.7) ─────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_session is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        // Ctrl+Click opens the active OSC 8 hyperlink (V5 §9.3). IExternalLauncher itself
        // enforces absolute http(s) only, so a non-web scheme simply does not open.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && _session.LastHyperlink is { } url)
        {
            _launcher.OpenUrl(url);
            e.Handled = true;
            return;
        }

        var cell = PointToCell(e.GetPosition(this));
        _selecting = true;
        _selAnchor = cell;
        _selHead = cell;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        // Phishing mitigation (V5 §9.3): surface the real OSC 8 target rather than the styled
        // link text. Per-cell hyperlink hit-testing needs buffer-view support it doesn't have
        // yet — the active-hyperlink target is shown while one is in effect (named gap).
        var link = _session?.LastHyperlink;
        ToolTip.SetTip(this, link);

        if (!_selecting)
            return;
        _selHead = PointToCell(e.GetPosition(this));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_selecting)
        {
            _selecting = false;
            e.Pointer.Capture(null);
        }
    }

    private (int Line, int Col) PointToCell(Point p)
    {
        var col = Math.Clamp((int)(p.X / _cellWidth), 0, Math.Max(0, _cols - 1));
        var screenRow = Math.Clamp((int)(p.Y / _cellHeight), 0, Math.Max(0, _rows - 1));
        var line = screenRow; // absolute mapping is resolved at copy time against the buffer view
        return (line, col);
    }

    private void CopySelection()
    {
        if (_session is null || _selAnchor is null || _selHead is null)
            return;

        var text = _session.ReadBuffer(view =>
        {
            var bottomTop = Math.Clamp(view.ViewportTop, 0, Math.Max(0, view.TotalLines - 1));
            var top = Math.Clamp(bottomTop - (int)Math.Round(_scrollOffsetLines), 0, bottomTop);

            var (a, b) = Ordered(_selAnchor.Value, _selHead.Value);
            var sb = new System.Text.StringBuilder();
            for (var sr = a.Line; sr <= b.Line; sr++)
            {
                var absolute = top + sr;
                var lineText = view.GetLineText(absolute);
                var from = sr == a.Line ? a.Col : 0;
                var to = sr == b.Line ? b.Col : lineText.Length;
                from = Math.Clamp(from, 0, lineText.Length);
                to = Math.Clamp(to, from, lineText.Length);
                sb.Append(lineText.AsSpan(from, to - from).ToString().TrimEnd());
                if (sr != b.Line)
                    sb.Append('\n');
            }
            return sb.ToString();
        });

        if (!string.IsNullOrEmpty(text))
            TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }

    private async Task PasteAsync()
    {
        if (_session is null)
            return;
        var clip = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clip is null)
            return;
#pragma warning disable CS0618 // GetTextAsync: the replacement extension needs IAsyncDataTransfer, not available on this Avalonia
        var text = await clip.GetTextAsync();
#pragma warning restore CS0618
        if (string.IsNullOrEmpty(text))
            return;

        var bracketed = _session.ReadBuffer(v => v.IsAlternateBuffer) || BracketedPasteEnabled();
        if (bracketed)
        {
            _session.Write("\x1b[200~"u8);
            _session.Write(System.Text.Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\r")));
            _session.Write("\x1b[201~"u8);
        }
        else
        {
            _session.Write(System.Text.Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\r")));
        }
        _scrollOffsetLines = 0;
    }

    private bool BracketedPasteEnabled() => _session?.BracketedPasteMode ?? false;

    private static ((int Line, int Col) A, (int Line, int Col) B) Ordered((int Line, int Col) x, (int Line, int Col) y)
        => (x.Line < y.Line || (x.Line == y.Line && x.Col <= y.Col)) ? (x, y) : (y, x);

    // ── key mapping ─────────────────────────────────────────────────────────────────────────

    private static TerminalModifiers ToModifiers(KeyModifiers m)
    {
        var r = TerminalModifiers.None;
        if (m.HasFlag(KeyModifiers.Shift)) r |= TerminalModifiers.Shift;
        if (m.HasFlag(KeyModifiers.Alt)) r |= TerminalModifiers.Alt;
        if (m.HasFlag(KeyModifiers.Control)) r |= TerminalModifiers.Control;
        return r;
    }

    private static bool TryMapKey(Key k, out TerminalKey t)
    {
        t = k switch
        {
            Key.Enter => TerminalKey.Enter,
            Key.Tab => TerminalKey.Tab,
            Key.Back => TerminalKey.Backspace,
            Key.Escape => TerminalKey.Escape,
            Key.Delete => TerminalKey.Delete,
            Key.Insert => TerminalKey.Insert,
            Key.Up => TerminalKey.Up,
            Key.Down => TerminalKey.Down,
            Key.Left => TerminalKey.Left,
            Key.Right => TerminalKey.Right,
            Key.Home => TerminalKey.Home,
            Key.End => TerminalKey.End,
            Key.PageUp => TerminalKey.PageUp,
            Key.PageDown => TerminalKey.PageDown,
            Key.F1 => TerminalKey.F1,
            Key.F2 => TerminalKey.F2,
            Key.F3 => TerminalKey.F3,
            Key.F4 => TerminalKey.F4,
            Key.F5 => TerminalKey.F5,
            Key.F6 => TerminalKey.F6,
            Key.F7 => TerminalKey.F7,
            Key.F8 => TerminalKey.F8,
            Key.F9 => TerminalKey.F9,
            Key.F10 => TerminalKey.F10,
            Key.F11 => TerminalKey.F11,
            Key.F12 => TerminalKey.F12,
            _ => (TerminalKey)(-1),
        };
        return (int)t >= 0;
    }

    internal static char? ControlCharFor(Key k)
    {
        if (k is >= Key.A and <= Key.Z)
            return (char)('a' + (k - Key.A));
        return k switch
        {
            Key.Space => ' ',
            Key.OemOpenBrackets => '[',
            Key.OemCloseBrackets => ']',
            Key.OemBackslash or Key.OemPipe => '\\',
            Key.OemMinus => '_',
            _ => null,
        };
    }

    /// <summary>
    /// Maps a key to the character to be prefixed with ESC under Alt (xterm metaSendsEscape).
    /// Letters respect Shift. Unshifted digits and period, comma, minus produce their ASCII character.
    /// Shifted symbols depend on the active keyboard layout and Windows generates them via WM_SYSCHAR
    /// which bypasses TextInput, so they are deliberately omitted.
    /// </summary>
    internal static char? MetaCharFor(Key k, bool shift)
    {
        if (k is >= Key.A and <= Key.Z)
            return shift ? (char)('A' + (k - Key.A)) : (char)('a' + (k - Key.A));

        if (!shift)
        {
            if (k is >= Key.D0 and <= Key.D9)
                return (char)('0' + (k - Key.D0));
            if (k is >= Key.NumPad0 and <= Key.NumPad9)
                return (char)('0' + (k - Key.NumPad0));

            return k switch
            {
                Key.OemPeriod => '.',
                Key.OemComma => ',',
                Key.OemMinus => '-',
                _ => null,
            };
        }

        return null;
    }

    /// <summary>
    /// Decides whether a focused terminal claims a chord as shell input rather than letting it
    /// trigger an application-level accelerator. Ctrl+` always toggles the dock.
    /// </summary>
    internal static bool ClaimsChord(Key key, KeyModifiers modifiers)
    {
        if (modifiers.HasFlag(KeyModifiers.Control) && key == Key.OemTilde)
            return false;

        if (modifiers == KeyModifiers.Control)
        {
            var ch = ControlCharFor(key);
            return ch is not null && KeyEncoder.EncodeControlChar(ch.Value, TerminalModifiers.Control) is not null;
        }

        if ((modifiers & ~KeyModifiers.Shift) == KeyModifiers.Alt)
        {
            return MetaCharFor(key, modifiers.HasFlag(KeyModifiers.Shift)) is not null;
        }

        return false;
    }

    // ── small helpers ───────────────────────────────────────────────────────────────────────

    private IBrush Brush(uint argb) => _brushes.Get(argb);
    private IPen Pen(uint argb, double thickness, int style) => _pens.Get(new PenKey(argb, thickness, style));

    private static uint ArgbOf(Rgb c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    private static uint Blend(uint fg, uint bg, double t)
    {
        byte L(int shift) => (byte)Math.Round(
            ((fg >> shift) & 0xFF) * (1 - t) + ((bg >> shift) & 0xFF) * t);
        return ((uint)0xFF << 24) | ((uint)L(16) << 16) | ((uint)L(8) << 8) | L(0);
    }
}
