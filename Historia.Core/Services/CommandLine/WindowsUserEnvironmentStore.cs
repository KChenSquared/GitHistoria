namespace Historia.Services.CommandLine;

/// <summary>
/// Windows implementation of <see cref="IUserEnvironmentStore"/> accessing the registry directly.
/// <para>
/// Note: <see cref="Environment.GetEnvironmentVariable(string, EnvironmentVariableTarget)"/> with
/// <see cref="EnvironmentVariableTarget.User"/> returns the value with environment variable references
/// (<c>%VAR%</c>) expanded, and <see cref="Environment.SetEnvironmentVariable(string, string?, EnvironmentVariableTarget)"/>
/// writes it back as <see cref="RegistryValueKind.String"/> (<c>REG_SZ</c>). A read-modify-write through
/// those APIs permanently replaces expandable entries such as <c>%USERPROFILE%</c> with literal paths.
/// In the .NET runtime source (<c>Environment.Win32.cs</c>), <c>GetEnvironmentVariableFromRegistry</c>
/// reads values without <see cref="RegistryValueOptions.DoNotExpandEnvironmentNames"/> and
/// <c>SetEnvironmentVariableFromRegistry</c> writes un-typed strings as <c>REG_SZ</c>.
/// Direct registry access with <see cref="RegistryValueOptions.DoNotExpandEnvironmentNames"/> and explicit
/// <see cref="RegistryValueKind"/> preserves original variable references and types.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUserEnvironmentStore : IUserEnvironmentStore
{
    private const string UserEnvironmentSubKey = "Environment";
    private const string MachineEnvironmentSubKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private const string PathValueName = "Path";

    private static readonly IntPtr HWND_BROADCAST = new(0xffff);
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    public (string? Value, RegistryValueKind Kind) ReadUserPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UserEnvironmentSubKey, writable: false);
        if (key is null)
            return (null, RegistryValueKind.ExpandString);

        var raw = key.GetValue(PathValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is null)
            return (null, RegistryValueKind.ExpandString);

        var kind = key.GetValueKind(PathValueName);
        return (raw.ToString(), kind);
    }

    public string? ReadMachinePath()
    {
        using var key = Registry.LocalMachine.OpenSubKey(MachineEnvironmentSubKey, writable: false);
        if (key is null)
            return null;

        var raw = key.GetValue(PathValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return raw?.ToString();
    }

    public void WriteUserPath(string value, RegistryValueKind kind)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UserEnvironmentSubKey, writable: true);
        key.SetValue(PathValueName, value, kind);
    }

    public void BroadcastEnvironmentChange()
    {
        SendMessageTimeout(
            HWND_BROADCAST,
            WM_SETTINGCHANGE,
            UIntPtr.Zero,
            "Environment",
            SMTO_ABORTIFHUNG,
            1000,
            out _);
    }

    // The process target has no registry round trip, so it keeps the text it is given.
    public string? ReadProcessPath() =>
        Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process);

    public void WriteProcessPath(string value) =>
        Environment.SetEnvironmentVariable("PATH", value, EnvironmentVariableTarget.Process);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        UIntPtr wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out UIntPtr lpdwResult);
}
