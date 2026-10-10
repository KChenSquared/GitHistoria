namespace Historia.Services.CommandLine;

/// <summary>
/// Abstraction over reading and writing the Windows user and machine environment variables and this process's PATH.
/// </summary>
[SupportedOSPlatform("windows")]
public interface IUserEnvironmentStore
{
    /// <summary>
    /// Reads the current user <c>Path</c> value and its registry value kind without expanding environment variable references.
    /// Returns <c>(null, RegistryValueKind.ExpandString)</c> when the value is missing.
    /// </summary>
    (string? Value, RegistryValueKind Kind) ReadUserPath();

    /// <summary>
    /// Reads the current machine <c>Path</c> value without expanding environment variable references.
    /// Returns <c>null</c> when missing or inaccessible.
    /// </summary>
    string? ReadMachinePath();

    /// <summary>
    /// Writes the user <c>Path</c> value preserving the specified registry kind.
    /// </summary>
    void WriteUserPath(string value, RegistryValueKind kind);

    /// <summary>
    /// Broadcasts <c>WM_SETTINGCHANGE</c> with lParam <c>"Environment"</c> to notify top-level windows of environment changes.
    /// </summary>
    void BroadcastEnvironmentChange();

    /// <summary>
    /// Reads this process's <c>PATH</c>.
    /// </summary>
    string? ReadProcessPath();

    /// <summary>
    /// Replaces this process's <c>PATH</c>, so programs it starts afterwards see the new value.
    /// </summary>
    void WriteProcessPath(string value);
}
