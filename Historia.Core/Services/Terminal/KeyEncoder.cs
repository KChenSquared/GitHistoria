namespace Historia.Services.Terminal;

/// <summary>Keys that do not arrive as printable text and must be encoded.</summary>
public enum TerminalKey
{
    Enter, Tab, Backspace, Escape, Delete, Insert,
    Up, Down, Left, Right, Home, End, PageUp, PageDown,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
}

[Flags]
public enum TerminalModifiers
{
    None = 0,
    Shift = 1,
    Alt = 2,
    Control = 4,
}

/// <summary>Emulator modes the encoding depends on. Sourced live from the emulator.</summary>
public readonly record struct TerminalModes(bool ApplicationCursor, bool ApplicationKeypad);

/// <summary>
/// Encodes a key press to the bytes a terminal application expects. Mode-dependent:
/// the same arrow key produces different bytes in normal vs. application-cursor mode. Pure,
/// Avalonia-free, and unit-tested byte-for-byte,
/// cross-checked against XTerm.NET's own key generator.
/// </summary>
public static class KeyEncoder
{
    private const byte Esc = 0x1B;

    public static ReadOnlySpan<byte> Encode(TerminalKey key, TerminalModifiers mods, TerminalModes modes)
    {
        switch (key)
        {
            case TerminalKey.Enter:
                return Prefixed(mods, (byte)'\r');
            case TerminalKey.Tab:
                return mods.HasFlag(TerminalModifiers.Shift) ? Csi("Z"u8) : Prefixed(mods & ~TerminalModifiers.Shift, (byte)'\t');
            case TerminalKey.Backspace:
                return Prefixed(mods, 0x7F);
            case TerminalKey.Escape:
                return Prefixed(mods, Esc);

            case TerminalKey.Up: return Cursor('A', mods, modes);
            case TerminalKey.Down: return Cursor('B', mods, modes);
            case TerminalKey.Right: return Cursor('C', mods, modes);
            case TerminalKey.Left: return Cursor('D', mods, modes);
            case TerminalKey.Home: return Cursor('H', mods, modes);
            case TerminalKey.End: return Cursor('F', mods, modes);

            case TerminalKey.Insert: return Tilde(2, mods);
            case TerminalKey.Delete: return Tilde(3, mods);
            case TerminalKey.PageUp: return Tilde(5, mods);
            case TerminalKey.PageDown: return Tilde(6, mods);

            case TerminalKey.F1: return FunctionSs3('P', mods);
            case TerminalKey.F2: return FunctionSs3('Q', mods);
            case TerminalKey.F3: return FunctionSs3('R', mods);
            case TerminalKey.F4: return FunctionSs3('S', mods);
            case TerminalKey.F5: return Tilde(15, mods);
            case TerminalKey.F6: return Tilde(17, mods);
            case TerminalKey.F7: return Tilde(18, mods);
            case TerminalKey.F8: return Tilde(19, mods);
            case TerminalKey.F9: return Tilde(20, mods);
            case TerminalKey.F10: return Tilde(21, mods);
            case TerminalKey.F11: return Tilde(23, mods);
            case TerminalKey.F12: return Tilde(24, mods);

            default:
                return ReadOnlySpan<byte>.Empty;
        }
    }

    /// <summary>
    /// Ctrl+letter and Ctrl+punctuation → the control code. Returns null when
    /// <paramref name="mods"/> lacks <see cref="TerminalModifiers.Control"/> or the character
    /// has no control mapping. <c>Ctrl+C</c> → <c>0x03</c>, <b>always</b>.
    /// </summary>
    public static byte? EncodeControlChar(char c, TerminalModifiers mods)
    {
        if (!mods.HasFlag(TerminalModifiers.Control))
            return null;

        if (c is >= 'a' and <= 'z') return (byte)(c - 'a' + 1);
        if (c is >= 'A' and <= 'Z') return (byte)(c - 'A' + 1);

        return c switch
        {
            ' ' or '@' => 0x00,
            '[' => 0x1B,
            '\\' => 0x1C,
            ']' => 0x1D,
            '^' => 0x1E,
            '_' => 0x1F,
            '?' => 0x7F,
            '/' => 0x1F,
            _ => null,
        };
    }

    /// <summary>
    /// Decides whether an unmapped key is claimed by the terminal or left unhandled so the
    /// platform character message (e.g. WM_CHAR on Windows) can reach <c>TextInput</c>.
    /// A handled <c>KeyDown</c> suppresses character input, so text-producing states (no
    /// modifier, Shift only, or Ctrl+Alt which represents AltGr on European layouts) must
    /// stay unhandled. Exactly one of Ctrl or Alt must be set to claim the key.
    /// </summary>
    public static bool ClaimsUnencodedKey(TerminalModifiers mods)
    {
        var ctrl = mods.HasFlag(TerminalModifiers.Control);
        var alt = mods.HasFlag(TerminalModifiers.Alt);
        return ctrl ^ alt;
    }

    /// <summary>
    /// Encodes an Alt+printable character as an ESC-prefixed byte sequence (xterm metaSendsEscape).
    /// Returns null when Alt is not held, Control is held, or the character is not printable ASCII (0x20–0x7E).
    /// </summary>
    public static byte[]? EncodeMeta(char ch, TerminalModifiers mods)
    {
        if (!mods.HasFlag(TerminalModifiers.Alt) || mods.HasFlag(TerminalModifiers.Control))
            return null;
        if (ch is < ' ' or > '~')
            return null;
        return new[] { Esc, (byte)ch };
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Modifier parameter per xterm: <c>1 + shift + alt*2 + ctrl*4</c>.</summary>
    private static int ModParam(TerminalModifiers mods)
        => 1 + (mods.HasFlag(TerminalModifiers.Shift) ? 1 : 0)
             + (mods.HasFlag(TerminalModifiers.Alt) ? 2 : 0)
             + (mods.HasFlag(TerminalModifiers.Control) ? 4 : 0);

    private static bool HasAny(TerminalModifiers mods) => mods != TerminalModifiers.None;

    /// <summary>An arrow / Home / End: <c>CSI &lt;final&gt;</c> or <c>SS3 &lt;final&gt;</c>, with the
    /// parameterised <c>CSI 1;n&lt;final&gt;</c> form when any modifier is held.</summary>
    private static ReadOnlySpan<byte> Cursor(char final, TerminalModifiers mods, TerminalModes modes)
    {
        if (HasAny(mods))
            return Ascii($"\x1b[1;{ModParam(mods)}{final}");

        return modes.ApplicationCursor
            ? Ascii($"\x1bO{final}")
            : Ascii($"\x1b[{final}");
    }

    /// <summary><c>CSI &lt;num&gt;~</c>, or <c>CSI &lt;num&gt;;n~</c> with a modifier.</summary>
    private static ReadOnlySpan<byte> Tilde(int num, TerminalModifiers mods)
        => HasAny(mods)
            ? Ascii($"\x1b[{num};{ModParam(mods)}~")
            : Ascii($"\x1b[{num}~");

    /// <summary>F1–F4: <c>SS3 P/Q/R/S</c>, or <c>CSI 1;nP</c> with a modifier.</summary>
    private static ReadOnlySpan<byte> FunctionSs3(char final, TerminalModifiers mods)
        => HasAny(mods)
            ? Ascii($"\x1b[1;{ModParam(mods)}{final}")
            : Ascii($"\x1bO{final}");

    /// <summary>A single control byte, ESC-prefixed when Alt is held.</summary>
    private static ReadOnlySpan<byte> Prefixed(TerminalModifiers mods, byte b)
        => mods.HasFlag(TerminalModifiers.Alt) ? new[] { Esc, b } : new[] { b };

    private static ReadOnlySpan<byte> Csi(ReadOnlySpan<byte> tail)
    {
        var buf = new byte[2 + tail.Length];
        buf[0] = Esc;
        buf[1] = (byte)'[';
        tail.CopyTo(buf.AsSpan(2));
        return buf;
    }

    private static ReadOnlySpan<byte> Ascii(string s) => Encoding.ASCII.GetBytes(s);
}
