namespace Historia.Services.Migrations;

/// <summary>
/// One step in a JSON schema migration pipeline. Migrations run in ascending
/// <see cref="FromVersion"/> order until the document reaches the app's current schema
/// version. Each migration mutates the <see cref="JsonObject"/> in place and is responsible
/// for setting <c>schemaVersion</c> to its target.
/// </summary>
public interface IJsonMigration
{
    /// <summary>The schema version this migration upgrades <b>from</b>.</summary>
    int FromVersion { get; }

    /// <summary>The schema version this migration produces.</summary>
    int ToVersion { get; }

    void Apply(JsonObject document);
}

/// <summary>Runs an ordered set of <see cref="IJsonMigration"/> steps over a JSON document.</summary>
public static class MigrationRunner
{
    /// <summary>
    /// Applies every applicable migration in <paramref name="migrations"/> to
    /// <paramref name="document"/>. A missing <c>schemaVersion</c> is treated as 0. Returns
    /// true when the document was changed.
    /// </summary>
    public static bool Run(JsonObject document, int currentVersion, IEnumerable<IJsonMigration> migrations)
    {
        var version = document["schemaVersion"]?.GetValue<int>() ?? 0;
        if (version >= currentVersion)
            return false;

        var ordered = migrations.OrderBy(m => m.FromVersion).ToList();
        var changed = false;

        while (version < currentVersion)
        {
            var step = ordered.FirstOrDefault(m => m.FromVersion == version);
            if (step is null)
            {
                // No path forward; stamp the current version and stop rather than loop forever.
                document["schemaVersion"] = currentVersion;
                return true;
            }

            step.Apply(document);
            version = step.ToVersion;
            changed = true;
        }

        return changed;
    }
}
