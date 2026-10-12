namespace Historia.ViewModels;

/// <summary>What fills the resolver's editor area.</summary>
public enum ConflictEditorMode
{
    /// <summary>The Ours, Theirs and Result panes of an editable text file.</summary>
    Editors,

    /// <summary>The card shown once no conflicted file remains.</summary>
    AllResolved,

    /// <summary>The card shown for a file the panes cannot edit (binary, submodule, too large).</summary>
    FileCard,
}

/// <summary>How many conflicts a file still carries when the user marks it resolved.</summary>
/// <param name="RemainingBlocks">Conflict-marker blocks (and damaged markers) left in the Result text.</param>
/// <param name="EmptiedConflicts">Managed conflicts whose selection keeps no line, so they save as nothing.</param>
public readonly record struct ConflictDocumentSummary(int RemainingBlocks, int EmptiedConflicts);

/// <summary>
/// The editor panes as the resolver view model sees them. The view owns the Ours, Theirs and
/// Result editors, their margins, the minimap and the selection session; the view model decides
/// when a file is loaded, saved or dropped and asks the surface to do the editor-side work.
/// </summary>
public interface IConflictEditorSurface
{
    /// <summary>The text a save writes: the Result with the selection session's choices applied.</summary>
    string ComposeOutput();

    /// <summary>A token for the Result document as it is now; pass it to <see cref="IsRevisionCurrent"/>.</summary>
    object CaptureRevision();

    /// <summary>True when the Result document has not changed since <paramref name="revision"/> was captured.</summary>
    bool IsRevisionCurrent(object revision);

    /// <summary>Marks the Result document as matching what is on disk, so undo stops at the loaded text.</summary>
    void MarkClean();

    /// <summary>The conflicts still in the Result text, for the mark-resolved warning.</summary>
    ConflictDocumentSummary Summarize();

    /// <summary>Puts <paramref name="text"/> in the Result for <paramref name="filePath"/> and drops the previous file's selection state.</summary>
    void LoadDocument(string filePath, string text);

    /// <summary>Makes the loaded text editable: parses it and aligns the Ours and Theirs panes to it.</summary>
    void EnterEditMode();

    /// <summary>Drops the parse and minimap state of an uneditable file.</summary>
    void LeaveEditMode();

    /// <summary>Drops the parse state and empties all three editors.</summary>
    void ClearDocument();

    /// <summary>Moves the caret to the next conflict, wrapping to the first.</summary>
    void GoToNextConflict();

    /// <summary>Moves the caret to the previous conflict, wrapping to the last.</summary>
    void GoToPreviousConflict();
}
