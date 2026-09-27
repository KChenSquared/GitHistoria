namespace Historia.Services.Terminal;

[Flags]
public enum TerminalCellFlags
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Blink = 16,
    Inverse = 32,
    Hidden = 64,
    Strikethrough = 128,
}

/// <summary>One resolved screen cell. A Core value type so XTerm.NET's own buffer
/// types never reach the App layer.</summary>
public readonly record struct TerminalCellSnapshot(
    int CodePoint,
    string Content,
    int Width,
    TerminalColor Foreground,
    TerminalColor Background,
    TerminalCellFlags Flags);

/// <summary>
/// A read-only view of the emulator screen buffer, handed to a callback that runs under the
/// session's gate. Nothing XTerm.NET owns escapes the callback.
/// </summary>
public interface ITerminalBufferView
{
    int Columns { get; }
    int Rows { get; }
    int CursorColumn { get; }
    int CursorRow { get; }
    bool CursorVisible { get; }
    bool IsAlternateBuffer { get; }

    /// <summary>Total lines available including scrollback.</summary>
    int TotalLines { get; }

    /// <summary>First visible absolute line index (scroll position).</summary>
    int ViewportTop { get; }

    /// <summary>Reads one cell by absolute line index and column. Out-of-range → an empty cell.</summary>
    TerminalCellSnapshot GetCell(int line, int column);

    /// <summary>Plain text of one absolute line (trailing blanks trimmed).</summary>
    string GetLineText(int line);
}
