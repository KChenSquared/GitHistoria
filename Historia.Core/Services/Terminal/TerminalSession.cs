namespace Historia.Services.Terminal;

public enum TerminalSessionState { Starting, Running, Exited, Failed }

/// <summary>
/// One live terminal: a pty, an <c>XTerm.NET</c> emulator, and the pump between them (V5 §3.1).
/// <para>
/// The emulator's screen buffer is guarded by a single-slot lock — the same discipline
/// <c>RepositoryContext</c> applies to its LibGit2Sharp handle. The pty read loop mutates on a
/// background thread; events are raised on the UI dispatcher (constitution rule 18).
/// <c>BufferChanged</c> is coalesced on a ~16 ms trailing window so a flood renders once per
/// frame, not once per chunk — the parser still sees every byte in order.
/// </para>
/// <para>
/// Deviation D1 (see the M1 spike doc): <c>XTerm.Terminal.Write</c> takes a <c>string</c>, so
/// this class owns one stateful <see cref="Decoder"/> that carries partial-UTF-8 state across
/// pty chunks — preserving the "no mangling at chunk boundaries" intent of design doc §2.
/// Deviation D2: XTerm.NET 1.2.0 exposes no damage event, so a row-hash diff here synthesises
/// the dirty-row set for the control.
/// </para>
/// </summary>
public sealed class TerminalSession : IDisposable
{
    private readonly IPtyConnection _pty;
    private readonly IUiDispatcher _dispatcher;
    private readonly XTermLib.Terminal _term;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly object _gate = new();
    private readonly IUiTimer _coalesceTimer;

    private long[] _rowHashes = [];
    private readonly HashSet<int> _dirtyRows = [];
    private bool _bufferDirtyPending;
    private bool _coalesceTimerArmed;
    private int _disposed;

    public Guid Id { get; } = Guid.NewGuid();
    public TerminalOrigin Origin { get; }
    public TerminalSessionState State { get; private set; } = TerminalSessionState.Starting;
    public int? ExitCode { get; private set; }

    /// <summary>OSC 0/2 title, already sanitised (V5 §9.3): control chars stripped, ≤ 60 chars.
    /// Null until the child sets one.</summary>
    public string? ReportedTitle { get; private set; }

    /// <summary>Last OSC 8 hyperlink target the child announced, if any (validated by the
    /// control before it is opened).</summary>
    public string? LastHyperlink { get; private set; }

    /// <summary>A one-line notice shown as the session's first line — e.g. the start directory
    /// fell back. Not a modal (V5 §4.2).</summary>
    public string? StartupNotice { get; }

    /// <summary>Live DEC key/keypad modes, read under the gate — the input the
    /// <see cref="KeyEncoder"/> needs (V5 §6.5).</summary>
    public TerminalModes CurrentModes
    {
        get { lock (_gate) return new TerminalModes(_term.ApplicationCursorKeys, _term.ApplicationKeypad); }
    }

    /// <summary>Whether the child has enabled bracketed-paste (DECSET 2004).</summary>
    public bool BracketedPasteMode
    {
        get { lock (_gate) return _term.BracketedPasteMode; }
    }

    public event EventHandler? BufferChanged;
    public event EventHandler? StateChanged;
    public event EventHandler? TitleChanged;

    public TerminalSession(
        TerminalOrigin origin,
        IPtyConnection pty,
        IUiDispatcher dispatcher,
        int columns,
        int rows,
        int scrollbackLines,
        string? startupNotice = null)
    {
        Origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _pty = pty ?? throw new ArgumentNullException(nameof(pty));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        StartupNotice = startupNotice;

        columns = Math.Clamp(columns, 1, 1000);
        rows = Math.Clamp(rows, 1, 1000);

        _term = new XTermLib.Terminal(new XTermLib.Options.TerminalOptions
        {
            Cols = columns,
            Rows = rows,
            Scrollback = Math.Max(0, scrollbackLines),
            TermName = "xterm-256color",
        });

        _coalesceTimer = _dispatcher.CreateTimer(TimeSpan.FromMilliseconds(16), OnCoalesceTick);

        _term.TitleChanged += OnEmulatorTitleChanged;
        _term.OscReceived += OnEmulatorOsc;
        _term.HyperlinkChanged += (_, e) => LastHyperlink = e.IsCleared ? null : e.Url;
        _term.DataReceived += OnEmulatorData;

        _pty.OutputReceived += OnPtyOutput;
        _pty.Exited += OnPtyExited;

        State = TerminalSessionState.Running;

        if (!string.IsNullOrEmpty(startupNotice))
            FeedString("\x1b[33m" + startupNotice + "\x1b[0m\r\n");
    }

    // ── input ───────────────────────────────────────────────────────────────────────────────

    public void Write(ReadOnlySpan<byte> input)
    {
        if (Volatile.Read(ref _disposed) != 0 || input.IsEmpty)
            return;
        _pty.Write(input);
    }

    public void Resize(int columns, int rows)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        columns = Math.Clamp(columns, 1, 1000);
        rows = Math.Clamp(rows, 1, 1000);

        lock (_gate)
        {
            _term.Resize(columns, rows);
            _rowHashes = [];          // force a full redraw after a reflow
        }
        _pty.Resize(columns, rows);
        MarkBufferDirty();
    }

    public void Kill() => _pty.Kill();

    // ── buffer read (under the gate) ────────────────────────────────────────────────────────

    /// <summary>Reads the emulator buffer under the session gate. Nothing XTerm.NET owns may
    /// escape <paramref name="read"/>.</summary>
    public T ReadBuffer<T>(Func<ITerminalBufferView, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_gate)
            return read(new BufferView(_term));
    }

    /// <summary>Returns the rows that changed since the last call and clears the set (V5 §6.3).</summary>
    public IReadOnlyList<int> TakeDirtyRows()
    {
        lock (_gate)
        {
            if (_dirtyRows.Count == 0)
                return [];
            var copy = _dirtyRows.ToArray();
            _dirtyRows.Clear();
            return copy;
        }
    }

    // ── pty → emulator pump ─────────────────────────────────────────────────────────────────

    private void OnPtyOutput(ReadOnlyMemory<byte> chunk)
    {
        if (Volatile.Read(ref _disposed) != 0 || chunk.IsEmpty)
            return;

        var bytes = chunk.Span;
        var chars = new char[_decoder.GetCharCount(bytes, flush: false)];
        var n = _decoder.GetChars(bytes, chars, flush: false);
        if (n == 0)
            return;

        FeedString(new string(chars, 0, n));
    }

    private void FeedString(string s)
    {
        lock (_gate)
        {
            _term.Write(s);
            RecomputeDirtyRows_NoLock();
        }
        MarkBufferDirty();
    }

    private void RecomputeDirtyRows_NoLock()
    {
        var buffer = _term.Buffer;
        var total = buffer.Length;
        if (_rowHashes.Length != total)
        {
            _rowHashes = new long[total];
            for (var y = 0; y < total; y++)
            {
                _rowHashes[y] = HashLine(y);
                _dirtyRows.Add(y);
            }
            return;
        }

        for (var y = 0; y < total; y++)
        {
            var h = HashLine(y);
            if (_rowHashes[y] != h)
            {
                _rowHashes[y] = h;
                _dirtyRows.Add(y);
            }
        }
    }

    private long HashLine(int y)
    {
        // FNV-1a over the line's text — cheap, process-stable, and enough to localise damage.
        const long offset = unchecked((long)14695981039346656037);
        const long prime = 1099511628211;
        var text = _term.GetLine(y) ?? string.Empty;
        var hash = offset;
        foreach (var ch in text)
            hash = (hash ^ ch) * prime;
        return hash;
    }

    private void OnEmulatorData(object? sender, XTermLib.Events.TerminalEvents.DataEventArgs e)
    {
        // The emulator's own replies (DSR, focus events, DECRQSS…) go straight back to the pty.
        if (!string.IsNullOrEmpty(e.Data))
            _pty.Write(Encoding.UTF8.GetBytes(e.Data));
    }

    // ── exit ────────────────────────────────────────────────────────────────────────────────

    private void OnPtyExited(int exitCode)
    {
        if (State is TerminalSessionState.Exited)
            return;

        ExitCode = exitCode;
        State = TerminalSessionState.Exited;
        FeedString($"\r\n\x1b[90m[process exited with code {exitCode}]\x1b[0m\r\n\x1b[90mPress Enter to start a new shell.\x1b[0m\r\n");
        RaiseOnDispatcher(StateChanged);
    }

    // ── OSC hardening (V5 §9.3) ─────────────────────────────────────────────────────────────

    private void OnEmulatorTitleChanged(object? sender, XTermLib.Events.TerminalEvents.TitleChangeEventArgs e)
        => ApplyReportedTitle(e.Title);

    private void OnEmulatorOsc(object? sender, XTermLib.Events.TerminalEvents.OscReceivedEventArgs e)
    {
        switch (e.Code)
        {
            case 0 or 2:
                ApplyReportedTitle(e.Data);
                break;
            case 52:
                // Clipboard write from the child — deliberately ignored (V5 §9.3).
                break;
        }
    }

    private void ApplyReportedTitle(string? raw)
    {
        var clean = SanitiseTitle(raw);
        if (clean is not null && LooksLikeExecutablePath(clean))
            clean = null;
        if (clean == ReportedTitle)
            return;
        ReportedTitle = clean;
        RaiseOnDispatcher(TitleChanged);
    }

    /// <summary>A Windows console reports the launching executable's full path as its initial
    /// OSC 0 title — e.g. <c>C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe</c>. On a
    /// pty resize (which every tab switch triggers) conhost re-emits it, so the tab caption
    /// flips from the origin name to that path. It is noise, not a caption: reject a rooted
    /// path that ends in <c>.exe</c> and keep falling back to <see cref="TerminalOrigin.Title"/>.</summary>
    internal static bool LooksLikeExecutablePath(string title)
        => title.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
           && (title.StartsWith('/') || title.StartsWith('\\')
               || (title.Length > 2 && char.IsAsciiLetter(title[0]) && title[1] == ':'));

    internal static string? SanitiseTitle(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return null;

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsControl(ch))
                continue;
            sb.Append(ch);
            if (sb.Length >= 60)
                break;
        }
        var s = sb.ToString().Trim();
        return s.Length == 0 ? null : s;
    }

    // ── coalescing ──────────────────────────────────────────────────────────────────────────

    private void MarkBufferDirty()
    {
        lock (_gate)
        {
            _bufferDirtyPending = true;

            // Called once per pty chunk — under heavy output that's thousands of calls a
            // second. Only the first call in a coalescing window needs to reach the UI thread
            // at all; the timer itself is what actually gates the render, and re-posting once
            // per chunk while a window is already open just floods the dispatcher queue.
            if (_coalesceTimerArmed)
                return;
            _coalesceTimerArmed = true;
        }

        _dispatcher.Post(() =>
        {
            if (Volatile.Read(ref _disposed) == 0)
                _coalesceTimer.Start();
        });
    }

    private void OnCoalesceTick()
    {
        bool raise;
        lock (_gate)
        {
            raise = _bufferDirtyPending;
            _bufferDirtyPending = false;
            _coalesceTimerArmed = false;
        }
        _coalesceTimer.Stop();
        if (raise)
            BufferChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseOnDispatcher(EventHandler? handler)
    {
        if (handler is null)
            return;
        _dispatcher.Post(() => handler(this, EventArgs.Empty));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _pty.OutputReceived -= OnPtyOutput;
        _pty.Exited -= OnPtyExited;
        _term.TitleChanged -= OnEmulatorTitleChanged;
        _term.OscReceived -= OnEmulatorOsc;
        _term.DataReceived -= OnEmulatorData;

        try { _coalesceTimer.Dispose(); } catch { /* best effort */ }
        try { _pty.Dispose(); } catch { /* best effort */ }
        try { _term.Dispose(); } catch { /* best effort */ }
    }

    // ── buffer view ─────────────────────────────────────────────────────────────────────────

    private sealed class BufferView(XTermLib.Terminal term) : ITerminalBufferView
    {
        public int Columns => term.Cols;
        public int Rows => term.Rows;
        public int CursorColumn => term.Buffer.X;
        public int CursorRow => term.Buffer.YBase + term.Buffer.Y;
        public bool CursorVisible => term.CursorVisible;
        public bool IsAlternateBuffer => term.IsAlternateBufferActive;
        public int TotalLines => term.Buffer.Length;
        public int ViewportTop => term.Buffer.YDisp;

        public string GetLineText(int line)
        {
            if (line < 0 || line >= term.Buffer.Length)
                return string.Empty;
            return (term.GetLine(line) ?? string.Empty).TrimEnd();
        }

        public TerminalCellSnapshot GetCell(int line, int column)
        {
            if (line < 0 || line >= term.Buffer.Length)
                return Empty;

            var bl = term.Buffer.GetLine(line);
            if (bl is null || column < 0 || column >= bl.Length)
                return Empty;

            var cell = bl[column];
            var a = cell.Attributes;

            var flags = TerminalCellFlags.None;
            if (a.IsBold()) flags |= TerminalCellFlags.Bold;
            if (a.IsDim()) flags |= TerminalCellFlags.Dim;
            if (a.IsItalic()) flags |= TerminalCellFlags.Italic;
            if (a.IsUnderline()) flags |= TerminalCellFlags.Underline;
            if (a.IsBlink()) flags |= TerminalCellFlags.Blink;
            if (a.IsInverse()) flags |= TerminalCellFlags.Inverse;
            if (a.IsInvisible()) flags |= TerminalCellFlags.Hidden;
            if (a.IsStrikethrough()) flags |= TerminalCellFlags.Strikethrough;

            return new TerminalCellSnapshot(
                cell.CodePoint,
                cell.Content ?? string.Empty,
                cell.Width,
                TerminalColor.FromEmulator(a.GetFgColor(), a.GetFgColorMode(), isBackground: false),
                TerminalColor.FromEmulator(a.GetBgColor(), a.GetBgColorMode(), isBackground: true),
                flags);
        }

        private static readonly TerminalCellSnapshot Empty =
            new(32, " ", 1, TerminalColor.DefaultForeground, TerminalColor.DefaultBackground, TerminalCellFlags.None);
    }
}
