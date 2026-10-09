namespace Historia.Diagnostics;

/// <summary>
/// Persists the count of consecutive log-trim failures across application runs.
/// Faults are absorbed silently: a missing, unreadable or corrupt file yields zero, and save failures are swallowed.
/// </summary>
internal sealed class LogTrimFailureStore
{
    private readonly string _path;

    public LogTrimFailureStore(string path)
    {
        _path = path;
    }

    public int Load()
    {
        try
        {
            if (!File.Exists(_path))
                return 0;

            var json = File.ReadAllText(_path);
            var node = JsonNode.Parse(json);
            if (node is JsonObject obj && obj.TryGetPropertyValue("consecutiveFailures", out var prop) && prop is not null)
            {
                var count = prop.GetValue<int>();
                return count < 0 ? 0 : count;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    public void Save(int count)
    {
        string? tempPath = null;
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            tempPath = _path + ".tmp." + Guid.NewGuid().ToString("N");
            var json = JsonSerializer.Serialize(new { consecutiveFailures = count });
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _path, overwrite: true);
            tempPath = null;
        }
        catch
        {
            // Diagnostics persistence must never throw.
        }
        finally
        {
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }
}
