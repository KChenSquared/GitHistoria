namespace Historia.Models;

/// <summary>
/// Architectural safety ceilings on resident graph layout.
/// Prevents runaway memory allocation when background walks traverse massive histories.
/// </summary>
public static class GraphLimits
{
    /// <summary>Default resident-row ceiling when per-commit metadata is paged from disk (the user can raise it — see <see cref="AppSettings.ResidentRowCap"/>).</summary>
    public const int MaxResidentRowsDisk = 2_500_000;

    /// <summary>Maximum resident rows when full CommitMetadata is resident in memory.</summary>
    public const int MaxResidentRowsMemory = 500_000;

    /// <summary>Resident-row ceiling forced while <see cref="AppSettings.LightweightMode"/> is on.</summary>
    public const int LightweightMaxResidentRows = 30_000;

    /// <summary>Background preload step forced while <see cref="AppSettings.LightweightMode"/> is on.</summary>
    public const int LightweightSilentCrawlRows = 10_000;

    /// <summary>Returns the ceiling on resident layout rows given the metadata storage mode,
    /// using <paramref name="residentRowCapDisk"/> (AppSettings.ResidentRowCap) as the
    /// disk-mode ceiling. When <paramref name="lightweight"/> is true, returns
    /// <see cref="LightweightMaxResidentRows"/> regardless of <paramref name="metaFromDisk"/>
    /// and the stored cap; bypassing the 500,000 floor is safe because the floor only guards an
    /// out-of-range stored value while the lightweight ceiling is a fixed positive constant.
    /// Clamped through <see cref="AppSettings.ClampResidentRowCap"/> centrally for normal mode,
    /// so a hand-edited or otherwise out-of-range settings value (e.g. <c>0</c>) can never stop
    /// the background spill or the graph from extending.</summary>
    public static int GetMaxResidentRows(bool metaFromDisk, int residentRowCapDisk, bool lightweight = false) =>
        lightweight
            ? LightweightMaxResidentRows
            : (metaFromDisk ? AppSettings.ClampResidentRowCap(residentRowCapDisk) : MaxResidentRowsMemory);

    /// <summary>Returns the background preload step for the silent crawler. When
    /// <paramref name="lightweight"/> is true, returns <see cref="LightweightSilentCrawlRows"/>;
    /// otherwise passes <paramref name="silentCrawlRowCap"/> through raw (<c>0</c> means uncapped).</summary>
    public static int GetSilentCrawlRowCap(int silentCrawlRowCap, bool lightweight) =>
        lightweight ? LightweightSilentCrawlRows : silentCrawlRowCap;
}
