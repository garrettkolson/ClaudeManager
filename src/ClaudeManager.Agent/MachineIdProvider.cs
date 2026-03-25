namespace ClaudeManager.Agent;

/// <summary>
/// Generates and persists a stable GUID for this machine.
/// Prevents collisions between machines that share a hostname.
/// Accepts an optional storagePath so the path can be overridden in tests
/// without touching the real %LocalAppData% directory.
/// </summary>
public static class MachineIdProvider
{
    public static readonly string DefaultStoragePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeManager",
        "machine_id");

    public static string GetOrCreate(string? storagePath = null)
    {
        var path = storagePath ?? DefaultStoragePath;

        if (File.Exists(path))
        {
            var stored = File.ReadAllText(path).Trim();
            if (Guid.TryParse(stored, out _))
                return stored;
        }

        var id = Guid.NewGuid().ToString();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, id);
        }
        catch
        {
            // Fall back to hostname if we can't persist; not ideal but functional
            return Environment.MachineName;
        }

        return id;
    }
}
