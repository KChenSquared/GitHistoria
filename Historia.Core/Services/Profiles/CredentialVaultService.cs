namespace Historia.Services.Profiles;

/// <inheritdoc cref="ICredentialVaultService"/>
public sealed class CredentialVaultService : ICredentialVaultService
{
    private const string TargetPrefix = "Historia/";
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    public Task StoreTokenAsync(string key, string token, CancellationToken ct = default)
        => Task.Run(() => Write(FullTarget(key), token), ct);

    public Task<string?> RetrieveTokenAsync(string key, CancellationToken ct = default)
        => Task.Run(() => Read(FullTarget(key)), ct);

    public Task DeleteTokenAsync(string key, CancellationToken ct = default)
        => Task.Run(() => Delete(FullTarget(key)), ct);

    private static string FullTarget(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return TargetPrefix + key;
    }

    private static void Write(string target, string secret)
    {
        var blob = Encoding.UTF8.GetBytes(secret);
        var blobHandle = Marshal.AllocHGlobal(blob.Length);
        var targetPtr = Marshal.StringToCoTaskMemUni(target);
        try
        {
            Marshal.Copy(blob, 0, blobHandle, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                CredentialBlob = blobHandle,
                CredentialBlobSize = blob.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };

            if (!CredWrite(ref cred, 0))
                throw new InvalidOperationException($"CredWrite failed: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.FreeHGlobal(blobHandle);
            Marshal.FreeCoTaskMem(targetPtr);
        }
    }

    private static string? Read(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var handle))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND)
                return null;
            throw new InvalidOperationException($"CredRead failed: {err}");
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(handle);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero)
                return string.Empty;

            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, cred.CredentialBlobSize);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(handle);
        }
    }

    private static void Delete(string target)
    {
        if (!CredDelete(target, CRED_TYPE_GENERIC, 0))
        {
            var err = Marshal.GetLastWin32Error();
            if (err != ERROR_NOT_FOUND)
                throw new InvalidOperationException($"CredDelete failed: {err}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
