namespace Historia.Services.CommandLine;

/// <inheritdoc cref="ICmdrPathService"/>
[SupportedOSPlatform("windows")]
public sealed class CmdrPathService : ICmdrPathService
{
    private const string CmdrExeName = "historia-cmdr.exe";
    private const string HistoriaExeName = "Historia.exe";

    private readonly IUserEnvironmentStore _store;
    private readonly string _ourFolder;
    private readonly Func<string, bool> _fileExists;
    private readonly string _backupFolder;
    private readonly Func<string, string, string> _saveBackup;
    private readonly IAppLog _log;

    public CmdrPathService(
        IUserEnvironmentStore store,
        string ourFolder,
        Func<string, bool> fileExists,
        string backupFolder,
        Func<string, string, string> saveBackup,
        IAppLog log)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(ourFolder);
        _ourFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ourFolder));
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
        _backupFolder = backupFolder ?? throw new ArgumentNullException(nameof(backupFolder));
        _saveBackup = saveBackup ?? throw new ArgumentNullException(nameof(saveBackup));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public CmdrPathStatus GetStatus()
    {
        var ourCmdrPath = Path.Combine(_ourFolder, CmdrExeName);
        if (!_fileExists(ourCmdrPath))
            return new CmdrPathStatus.Unavailable();

        string? userPath;
        PathChange plan;
        try
        {
            (userPath, _) = _store.ReadUserPath();
            plan = UserPathEditor.PlanReplace(userPath, _ourFolder, HoldsCmdr);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            _log.Warn("cmdr-path", "Could not read the user PATH", ex);
            return new CmdrPathStatus.Unavailable();
        }

        if (plan.IsNoOp)
            return new CmdrPathStatus.Current();

        var machineCopies = FindMachineCopies();
        return new CmdrPathStatus.NeedsUpdate(plan, machineCopies, _ourFolder, _backupFolder);
    }

    public CmdrPathApplyResult Apply(PathChange confirmed)
    {
        ArgumentNullException.ThrowIfNull(confirmed);

        if (confirmed.TooLong)
            return new CmdrPathApplyResult.Failed("PATH value would exceed maximum allowed length of 32,767 characters.");

        if (!_fileExists(Path.Combine(_ourFolder, CmdrExeName)))
            return new CmdrPathApplyResult.Failed("historia-cmdr is unavailable.");

        string? currentValue;
        RegistryValueKind kind;
        try
        {
            (currentValue, kind) = _store.ReadUserPath();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return new CmdrPathApplyResult.Failed(ex.Message);
        }

        // Comparing the value read against the one the preview was planned from, not just the
        // results, keeps an entry added while the dialog was open from being removed unconfirmed.
        if (!string.Equals(currentValue, confirmed.Original, StringComparison.Ordinal))
        {
            return string.Equals(currentValue, confirmed.NewValue, StringComparison.Ordinal)
                ? new CmdrPathApplyResult.AlreadyCurrent()
                : new CmdrPathApplyResult.Changed();
        }

        var newPlan = UserPathEditor.PlanReplace(currentValue, _ourFolder, HoldsCmdr);
        if (newPlan.IsNoOp)
            return new CmdrPathApplyResult.AlreadyCurrent();

        // The folders on PATH can gain or lose a historia-cmdr between preview and confirm.
        if (!string.Equals(newPlan.NewValue, confirmed.NewValue, StringComparison.Ordinal)
            || !newPlan.Removed.SequenceEqual(confirmed.Removed, StringComparer.Ordinal))
            return new CmdrPathApplyResult.Changed();

        string backupPath;
        try
        {
            var backupContent = PathBackup.ToRegFile(currentValue ?? string.Empty, kind);
            backupPath = _saveBackup(_backupFolder, backupContent);
        }
        catch (Exception ex)
        {
            return new CmdrPathApplyResult.Failed(ex.Message);
        }

        try
        {
            _store.WriteUserPath(newPlan.NewValue, kind);
            _store.BroadcastEnvironmentChange();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return new CmdrPathApplyResult.Failed(ex.Message);
        }

        try
        {
            var processPlan = UserPathEditor.PlanReplace(_store.ReadProcessPath(), _ourFolder, HoldsCmdr);
            _store.WriteProcessPath(processPlan.NewValue);
        }
        catch
        {
            // Process environment update is best-effort.
        }

        return new CmdrPathApplyResult.Written(backupPath);
    }

    private bool HoldsCmdr(string folder)
    {
        var cmdr = Path.Combine(folder, CmdrExeName);
        var app = Path.Combine(folder, HistoriaExeName);
        return _fileExists(cmdr) && _fileExists(app);
    }

    private IReadOnlyList<string> FindMachineCopies()
    {
        string? machinePath;
        try
        {
            machinePath = _store.ReadMachinePath();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            _log.Warn("cmdr-path", "Could not read the machine PATH", ex);
            return [];
        }

        if (string.IsNullOrWhiteSpace(machinePath))
            return [];

        var entries = UserPathEditor.Split(machinePath);
        var copies = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var expanded = UserPathEditor.ExpandEntry(entry);
            if (expanded is null || UserPathEditor.SameFolder(entry, _ourFolder))
                continue;

            try
            {
                if (seen.Add(expanded))
                {
                    var cmdr = Path.Combine(expanded, CmdrExeName);
                    if (_fileExists(cmdr))
                        copies.Add(entry);
                }
            }
            catch
            {
                // Unresolvable entry
            }
        }

        return copies;
    }

    /// <summary>
    /// Default backup file saver writing timestamped .reg files under <paramref name="folder"/>.
    /// </summary>
    public static string DefaultSaveBackup(string folder, string contents)
    {
        Directory.CreateDirectory(folder);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var baseName = $"user-path-{timestamp}";
        var targetPath = Path.Combine(folder, $"{baseName}.reg");
        int counter = 2;
        while (File.Exists(targetPath))
        {
            targetPath = Path.Combine(folder, $"{baseName}-{counter}.reg");
            counter++;
        }
        File.WriteAllText(targetPath, contents, Encoding.Unicode);
        return targetPath;
    }
}
