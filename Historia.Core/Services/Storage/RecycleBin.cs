namespace Historia.Services.Storage;

/// <summary>
/// Removes a file or directory, routing it to the OS recycle bin / trash where the platform
/// supports that (Windows, via <c>SHFileOperation</c> with <c>FOF_ALLOWUNDO</c>) and falling
/// back to a permanent delete otherwise. Used by the "discard changes" paths so that wiping a
/// never-committed file is recoverable rather than gone for good.
/// </summary>
internal static class RecycleBin
{
    /// <summary>Send <paramref name="fullPath"/> (an absolute file or directory path) to the
    /// recycle bin on Windows; permanently delete it on other platforms, or if the shell call
    /// fails or is unavailable. A no-op when the path does not exist.</summary>
    public static void Delete(string fullPath)
    {
        // Normalise separators (repo paths arrive forward-slashed; SHFileOperation wants '\').
        fullPath = Path.GetFullPath(fullPath);
        var isFile = File.Exists(fullPath);
        var isDir = !isFile && Directory.Exists(fullPath);
        if (!isFile && !isDir)
            return;

        if (OperatingSystem.IsWindows() && TryRecycleWindows(fullPath))
            return;

        if (isFile)
            File.Delete(fullPath);
        else
            Directory.Delete(fullPath, recursive: true);
    }

    [SupportedOSPlatform("windows")]
    private static bool TryRecycleWindows(string fullPath)
    {
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                // SHFileOperation wants a double-null-terminated list; the LPWStr marshaller
                // preserves the embedded null and appends the outer terminator.
                pFrom = fullPath + '\0',
                fFlags = FOF_ALLOWUNDO | FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI,
            };
            var rc = SHFileOperation(ref op);
            return rc == 0 && !op.fAnyOperationsAborted;
        }
        catch
        {
            return false;
        }
    }

    private const int FO_DELETE = 0x0003;
    private const short FOF_SILENT = 0x0004;
    private const short FOF_NOCONFIRMATION = 0x0010;
    private const short FOF_ALLOWUNDO = 0x0040;
    private const short FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public int wFunc;
        public string pFrom;
        public string? pTo;
        public short fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
