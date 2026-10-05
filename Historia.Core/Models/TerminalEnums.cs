namespace Historia.Models;

/// <summary>Shape of the terminal caret. The running application may override this
/// live via DECSCUSR; this is only the default.</summary>
public enum TerminalCursorStyle
{
    Block,
    Underline,
    Bar,
}
