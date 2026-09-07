namespace Historia.Models;

/// <summary>Shape of the terminal caret (V5 §13). The running application may override this
/// live via DECSCUSR; this is only the default.</summary>
public enum TerminalCursorStyle
{
    Block,
    Underline,
    Bar,
}

/// <summary>How the terminal reacts to a BEL (<c>\a</c>) from the child (V5 §13).</summary>
public enum TerminalBellStyle
{
    /// <summary>Ignore it entirely.</summary>
    None,

    /// <summary>A single low-alpha frame flash of <c>term-fg</c> (V5 §17 open question 3).</summary>
    Visual,
}
