namespace Historia.Services.CommandLine;

/// <summary>
/// Status of historia-cmdr on the user PATH.
/// </summary>
public abstract record CmdrPathStatus
{
    /// <summary>historia-cmdr is not supported or not present beside Historia.exe.</summary>
    public sealed record Unavailable : CmdrPathStatus;

    /// <summary>Historia's folder is already the only historia-cmdr folder on the user PATH.</summary>
    public sealed record Current : CmdrPathStatus;

    /// <summary>The user PATH can be updated to include or replace historia-cmdr.</summary>
    public sealed record NeedsUpdate(PathChange Preview, IReadOnlyList<string> MachineCopies) : CmdrPathStatus;
}

/// <summary>
/// Result of applying a proposed PATH change.
/// </summary>
public abstract record CmdrPathApplyResult
{
    /// <summary>The user PATH was updated and backed up.</summary>
    public sealed record Written(string BackupPath) : CmdrPathApplyResult;

    /// <summary>The user PATH changed in the registry since the preview was calculated.</summary>
    public sealed record Changed : CmdrPathApplyResult;

    /// <summary>Writing the backup or registry failed with an error message.</summary>
    public sealed record Failed(string Message) : CmdrPathApplyResult;
}

/// <summary>
/// Coordinates checking and updating the user PATH for historia-cmdr.
/// </summary>
public interface ICmdrPathService
{
    /// <summary>
    /// Gets the current status of historia-cmdr on PATH.
    /// </summary>
    CmdrPathStatus GetStatus();

    /// <summary>
    /// Applies a confirmed PATH change after verifying it has not become stale.
    /// </summary>
    CmdrPathApplyResult Apply(PathChange confirmed);
}
