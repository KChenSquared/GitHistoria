namespace Historia.Theming;

/// <summary>
/// Application singleton managing TextMate syntax highlighting themes and grammar installations.
/// Lazily builds a single shared registry options wrapper and manages live TextMate installations.
/// </summary>
public sealed class SyntaxThemeHost : IDisposable
{
    private readonly IThemeService _themeService;
    private readonly HashSet<TextMate.Installation> _liveInstallations = [];
    private readonly object _lock = new();
    private RegistryOptionsWrapper? _wrapper;
    private bool _disposed;

    internal int LiveCount
    {
        get
        {
            lock (_lock)
            {
                return _liveInstallations.Count;
            }
        }
    }

    public SyntaxThemeHost(IThemeService themeService)
    {
        _themeService = themeService;
        _themeService.ThemeChanged += OnThemeChanged;
    }

    private void EnsureInitialized()
    {
        if (_wrapper is not null)
        {
            return;
        }

        lock (_lock)
        {
            if (_wrapper is null)
            {
                var initialTheme = MapTheme(_themeService.Effective);
                _wrapper = new RegistryOptionsWrapper(initialTheme);
            }
        }
    }

    /// <summary>
    /// Attaches TextMate syntax highlighting to an editor.
    /// Returns an IDisposable that disposes the installation and detaches it from the host.
    /// </summary>
    public IDisposable Attach(TextEditor editor, string? filePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        var installation = editor.InstallTextMate(_wrapper!);
        lock (_lock)
        {
            _liveInstallations.Add(installation);
        }

        var currentTheme = MapTheme(_themeService.Effective);
        installation.SetTheme(_wrapper!.LoadTheme(currentTheme));

        if (!string.IsNullOrEmpty(filePath))
        {
            var scope = _wrapper.GetScopeByPath(filePath);
            if (!string.IsNullOrEmpty(scope))
            {
                installation.SetGrammar(scope);
            }
        }

        return new AttachSubscription(() =>
        {
            lock (_lock)
            {
                _liveInstallations.Remove(installation);
            }
            installation.Dispose();
        });
    }

    public static ThemeName MapTheme(EffectiveTheme theme) => theme switch
    {
        EffectiveTheme.Light => ThemeName.LightPlus,
        EffectiveTheme.HighContrast => ThemeName.HighContrastDark,
        _ => ThemeName.DarkPlus,
    };

    internal IRawTheme LoadTheme(EffectiveTheme theme)
    {
        EnsureInitialized();
        return _wrapper!.LoadTheme(MapTheme(theme));
    }

    internal IRawTheme LoadTheme(ThemeName name)
    {
        EnsureInitialized();
        return _wrapper!.LoadTheme(name);
    }

    internal string GetScopeByPath(string filePath)
    {
        EnsureInitialized();
        return _wrapper!.GetScopeByPath(filePath);
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var themeName = MapTheme(e.NewTheme);

        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyThemeToLiveInstallations(themeName);
        }
        else
        {
            Dispatcher.UIThread.Post(() => ApplyThemeToLiveInstallations(themeName));
        }
    }

    private void ApplyThemeToLiveInstallations(ThemeName themeName)
    {
        if (_disposed || _wrapper is null)
        {
            return;
        }

        var rawTheme = _wrapper.LoadTheme(themeName);
        TextMate.Installation[] targets;
        lock (_lock)
        {
            targets = _liveInstallations.ToArray();
        }

        foreach (var installation in targets)
        {
            try
            {
                installation.SetTheme(rawTheme);
            }
            catch
            {
                // Tolerate any detached or concurrently disposed editors
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _themeService.ThemeChanged -= OnThemeChanged;

        TextMate.Installation[] targets;
        lock (_lock)
        {
            targets = _liveInstallations.ToArray();
            _liveInstallations.Clear();
        }

        foreach (var installation in targets)
        {
            try
            {
                installation.Dispose();
            }
            catch
            {
            }
        }
    }

    private sealed class AttachSubscription(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose()
        {
            Interlocked.Exchange(ref _onDispose, null)?.Invoke();
        }
    }

    internal sealed class RegistryOptionsWrapper(ThemeName defaultTheme) : IRegistryOptions
    {
        private readonly RegistryOptions _backend = new(defaultTheme);
        private readonly Dictionary<string, IRawGrammar?> _rawGrammarCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ThemeName, IRawTheme> _themeCache = [];
        private readonly object _cacheLock = new();

        public IRawTheme GetTheme(string scopeName) => _backend.GetTheme(scopeName);
        public IRawTheme GetDefaultTheme() => _backend.GetDefaultTheme();
        public ICollection<string> GetInjections(string scopeName) => _backend.GetInjections(scopeName);

        public IRawGrammar GetGrammar(string scopeName)
        {
            if (string.IsNullOrEmpty(scopeName))
            {
                return null!;
            }

            lock (_cacheLock)
            {
                if (_rawGrammarCache.TryGetValue(scopeName, out var cached))
                {
                    return cached!;
                }

                var grammar = _backend.GetGrammar(scopeName);
                _rawGrammarCache[scopeName] = grammar;
                return grammar!;
            }
        }

        public string GetScope(string filename) => GetScopeByPath(filename);

        public string GetScopeByPath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return string.Empty;
            }

            var extension = System.IO.Path.GetExtension(filePath);
            if (string.IsNullOrEmpty(extension))
            {
                return string.Empty;
            }

            if (extension.Equals(".h", StringComparison.OrdinalIgnoreCase))
            {
                extension = ".cpp";
            }
            else if (extension.Equals(".resx", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".plist", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".manifest", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".axaml", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".targets", StringComparison.OrdinalIgnoreCase))
            {
                extension = ".xml";
            }
            else if (extension.Equals(".command", StringComparison.OrdinalIgnoreCase))
            {
                extension = ".sh";
            }

            return _backend.GetScopeByExtension(extension) ?? string.Empty;
        }

        public IRawTheme LoadTheme(ThemeName name)
        {
            lock (_cacheLock)
            {
                if (_themeCache.TryGetValue(name, out var cached))
                {
                    return cached;
                }

                var loaded = _backend.LoadTheme(name);
                _themeCache[name] = loaded;
                return loaded;
            }
        }
    }
}
