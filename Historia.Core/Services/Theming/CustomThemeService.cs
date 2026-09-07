namespace Historia.Services.Theming;

/// <summary>§V6-G9: enumerate / load / save / validate the user's custom themes, and compose a
/// theme against its built-in base into a concrete <see cref="ResolvedPalette"/>. Singleton.</summary>
public interface ICustomThemeService
{
    /// <summary>All valid themes under <c>themes/</c>, sorted by name (ordinal). Cached.</summary>
    IReadOnlyList<CustomTheme> List();

    /// <summary>The theme with this id, or null if it is missing or invalid.</summary>
    CustomTheme? Get(string id);

    /// <summary>Write the theme atomically, refresh the cache, raise <see cref="Changed"/>.</summary>
    Task<CustomTheme> SaveAsync(CustomTheme theme, CancellationToken ct = default);

    /// <summary>Delete the theme's file. No-op if it does not exist.</summary>
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>A fresh, empty theme over the given base (<c>"Dark"</c>/<c>"Light"</c>).</summary>
    CustomTheme NewFrom(string baseName);

    /// <summary>The base built-in with <paramref name="theme"/>'s overrides applied.</summary>
    ResolvedPalette Compose(CustomTheme theme);

    event EventHandler? Changed;
}

/// <inheritdoc cref="ICustomThemeService"/>
public sealed class CustomThemeService : ICustomThemeService
{
    private readonly object _sync = new();
    private List<CustomTheme>? _cache;

    public event EventHandler? Changed;

    public IReadOnlyList<CustomTheme> List()
    {
        lock (_sync)
        {
            if (_cache is not null)
                return _cache;

            var list = new List<CustomTheme>();
            try
            {
                var dir = AppPaths.ThemesDirectory;
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                    {
                        var theme = TryLoad(file);
                        if (theme is not null)
                            list.Add(theme);
                    }
                }
            }
            catch { /* rule 0.4: a broken themes dir just yields no custom themes */ }

            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            _cache = list;
            return _cache;
        }
    }

    public CustomTheme? Get(string id) =>
        List().FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    public async Task<CustomTheme> SaveAsync(CustomTheme theme, CancellationToken ct = default)
    {
        try { Directory.CreateDirectory(AppPaths.ThemesDirectory); } catch { /* rule 0.4 */ }

        var path = AppPaths.CustomThemeFile(theme.Id);
        var json = JsonSerializer.Serialize(theme, HistoriaJson.Options);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);

        Invalidate();
        Changed?.Invoke(this, EventArgs.Empty);
        return theme;
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var path = AppPaths.CustomThemeFile(id);
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }

        Invalidate();
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public CustomTheme NewFrom(string baseName)
    {
        var baseNorm = string.Equals(baseName, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        var n = List().Count + 1;
        return new CustomTheme
        {
            Id = "theme-" + Guid.NewGuid().ToString("n")[..8],
            Name = $"Custom {n}",
            Base = baseNorm,
            // "Importing from Light / Dark" also brings that mode's splash location across, so
            // the editor opens with something to tweak rather than an empty field.
            SplashImagePath = CustomTheme.BuiltInSplashLocation(baseNorm),
            SplashSeconds = 2,
        };
    }

    public ResolvedPalette Compose(CustomTheme theme)
    {
        var baseTokens = HistoriaPalette.For(theme.BaseTheme);
        var tokens = new Dictionary<string, Rgb>(baseTokens, StringComparer.Ordinal);

        foreach (var (key, hex) in theme.Tokens)
        {
            if (!baseTokens.ContainsKey(key))
                continue; // unknown token — dropped on load
            if (TryParse(hex, out var rgb))
                tokens[key] = rgb;
        }

        var lanes = new Rgb[LanePalette.Size];
        for (var i = 0; i < lanes.Length; i++)
        {
            lanes[i] = LanePalette.ColorFor(i, theme.BaseTheme);
            if (i < theme.Lane.Count && theme.Lane[i] is { } h && TryParse(h, out var rgb))
                lanes[i] = rgb;
        }

        return new ResolvedPalette(tokens, lanes);
    }

    private void Invalidate()
    {
        lock (_sync) _cache = null;
    }

    private static CustomTheme? TryLoad(string file)
    {
        try
        {
            var theme = JsonSerializer.Deserialize<CustomTheme>(File.ReadAllText(file), HistoriaJson.Options);
            if (theme is null || string.IsNullOrWhiteSpace(theme.Id) || string.IsNullOrWhiteSpace(theme.Name))
                return null;

            // §V6-G9: every hex the file DOES specify must parse, or the theme is rejected
            // outright — never a half-applied theme.
            foreach (var hex in theme.Tokens.Values)
                if (!TryParse(hex, out _))
                    return null;
            foreach (var hex in theme.Lane)
                if (hex is not null && !TryParse(hex, out _))
                    return null;

            // Pin the id to the file stem so a renamed file cannot desync from settings.
            var stem = Path.GetFileNameWithoutExtension(file);
            return theme.Id == stem ? theme : theme with { Id = stem };
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParse(string hex, out Rgb rgb)
    {
        try
        {
            rgb = Rgb.Parse(hex);
            return true;
        }
        catch
        {
            rgb = default;
            return false;
        }
    }
}
