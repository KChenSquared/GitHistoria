namespace Historia.Diagnostics;

/// <summary>
/// Opt-in gate shared by <see cref="AppLog"/> and <see cref="PerfLog"/>.
/// Resolves whether logging is enabled by environment variable, marker flag file,
/// or flow-local test override, and persists on/off transitions to the marker file.
/// </summary>
internal sealed class OptInFlag
{
    private readonly string _envVar;
    private readonly string _flagPath;
    private bool _enabled;
    private readonly AsyncLocal<bool?> _testOverride = new();

    public OptInFlag(string envVar, string flagFileName)
    {
        _envVar = envVar;
        _flagPath = Path.Combine(AppPaths.MachineDirectory, flagFileName);
        _enabled = EnvOptIn() || FlagFileExists();
    }

    /// <summary>
    /// Path to the marker flag file.
    /// </summary>
    public string FlagPath => _flagPath;

    /// <summary>
    /// True when either the environment variable is set or the flag file currently exists on disk.
    /// </summary>
    public bool IsOptedIn => EnvOptIn() || FlagFileExists();

    /// <summary>
    /// Gets whether logging is enabled. Setting it persists the choice to the marker flag file.
    /// </summary>
    public bool Enabled
    {
        get => _testOverride.Value ?? Volatile.Read(ref _enabled);
        set
        {
            if (_testOverride.Value.HasValue)
            {
                _testOverride.Value = value;
                return;
            }

            _enabled = value;
            PersistFlag(value);
        }
    }

    /// <summary>
    /// Test seam: overrides Enabled for the current async flow without touching the flag file.
    /// </summary>
    public void OverrideForTesting(bool? enabled)
    {
        _testOverride.Value = enabled;
    }

    /// <summary>
    /// Test seam: updates memory state without touching disk.
    /// </summary>
    public void SetEnabledInMemory(bool enabled)
    {
        _enabled = enabled;
    }

    public void PersistFlag(bool on)
    {
        try
        {
            if (on)
                File.WriteAllText(_flagPath, "");
            else if (File.Exists(_flagPath))
                File.Delete(_flagPath);
        }
        catch { /* diagnostics must never throw */ }
    }

    public bool EnvOptIn() => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(_envVar));

    public bool FlagFileExists()
    {
        try { return File.Exists(_flagPath); }
        catch { return false; }
    }
}
