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
    private readonly Action<string> _updateProcessPath;

    public CmdrPathService(
        IUserEnvironmentStore store,
        string ourFolder,
        Func<string, bool> fileExists,
        string backupFolder,
        Func<string, string, string> saveBackup,
        Action<string>? updateProcessPath = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _ourFolder = ourFolder ?? throw new ArgumentNullException(nameof(ourFolder));
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
        _backupFolder = backupFolder ?? throw new ArgumentNullException(nameof(backupFolder));
        _saveBackup = saveBackup ?? throw new ArgumentNullException(nameof(saveBackup));
        _updateProcessPath = updateProcessPath ?? (path => Environment.SetEnvironmentVariable("PATH", path, EnvironmentVariableTarget.Process));
    }

    public CmdrPathStatus GetStatus()
    {
        if (!OperatingSystem.IsWindows())
            return new CmdrPathStatus.Unavailable();

        var ourCmdrPath = Path.Combine(_ourFolder, CmdrExeName);
        if (!_fileExists(ourCmdrPath))
            return new CmdrPathStatus.Unavailable();

        var (userPath, _) = _store.ReadUserPath();
        var plan = UserPathEditor.PlanReplace(userPath, _ourFolder, HoldsCmdr);

        if (plan.IsNoOp)
            return new CmdrPathStatus.Current();

        var machineCopies = FindMachineCopies();
        return new CmdrPathStatus.NeedsUpdate(plan, machineCopies);
    }

    public CmdrPathApplyResult Apply(PathChange confirmed)
    {
        ArgumentNullException.ThrowIfNull(confirmed);

        if (confirmed.TooLong)
            return new CmdrPathApplyResult.Failed("PATH value would exceed maximum allowed length of 32,767 characters.");

        if (!OperatingSystem.IsWindows() || !_fileExists(Path.Combine(_ourFolder, CmdrExeName)))
            return new CmdrPathApplyResult.Failed("historia-cmdr is unavailable.");

        var (currentValue, kind) = _store.ReadUserPath();
        var newPlan = UserPathEditor.PlanReplace(currentValue, _ourFolder, HoldsCmdr);

        if (!string.Equals(newPlan.NewValue, confirmed.NewValue, StringComparison.Ordinal))
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
            var processPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process);
            var processPlan = UserPathEditor.PlanReplace(processPath, _ourFolder, HoldsCmdr);
            _updateProcessPath(processPlan.NewValue);
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
        var machinePath = _store.ReadMachinePath();
        if (string.IsNullOrWhiteSpace(machinePath))
            return [];

        var entries = UserPathEditor.Split(machinePath);
        var copies = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(entry).Trim().Trim('"');
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
