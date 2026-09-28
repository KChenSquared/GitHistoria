namespace Historia.Services.Git;

/// <summary>Best-effort worktree file size lookup used during status parsing — a missing or
/// inaccessible file is not an error here, it just sizes as 0.</summary>
internal static class WorkingTreeFileSize
{
    public static long GetOrZero(string fullPath)
    {
        try
        {
            if (File.Exists(fullPath))
                return new FileInfo(fullPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // size is best-effort
        }
        return 0;
    }
}
