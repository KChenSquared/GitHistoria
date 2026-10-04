namespace Historia.Services.Terminal;

/// <summary>Which keyboard event a key encoding describes.</summary>
public enum TerminalKeyEventKind { Press, Repeat, Release }

/// <summary>
/// One key for the Kitty keyboard encoder. <paramref name="Key"/> and <paramref name="Code"/> use
/// browser <c>KeyboardEvent.key</c> / <c>KeyboardEvent.code</c> naming ("a", "Enter", "ArrowUp";
/// "KeyA", "ShiftLeft", "Numpad7"), because that is what the emulator's encoder expects.
/// <paramref name="Code"/> is empty when the physical key is unknown.
/// </summary>
public readonly record struct TerminalKeyInput(string Key, string Code, TerminalModifiers Modifiers);

/// <summary>The Kitty keyboard protocol state the running program has negotiated.
/// <paramref name="KittyActive"/> is false when the setting is off or no flags are pushed.</summary>
public readonly record struct TerminalKeyboardProtocol(bool KittyActive, bool ReportsEventTypes, bool ReportsAllKeys);

/// <summary>Text a program asked to put on the system clipboard through OSC 52. Already
/// filtered: non-empty, within <see cref="TerminalSession.MaxClipboardWriteBytes"/>, NUL-free.</summary>
public sealed class TerminalClipboardWriteEventArgs(string text) : EventArgs
{
    public string Text { get; } = text;
}
