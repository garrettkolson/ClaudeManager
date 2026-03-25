namespace ClaudeManager.Agent;

/// <summary>
/// Generates and persists a stable GUID for this machine.
/// Prevents collisions between machines that share a hostname.
/// </summary>
public static class MachineIdProvider
{
    private static readonly string StoragePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeManager",
        "machine_id");

    public static string GetOrCreate()
    {
        if (File.Exists(StoragePath))
        {
            var stored = File.ReadAllText(StoragePath).Trim();
            if (Guid.TryParse(stored, out _))
                return stored;
        }

        var id = Guid.NewGuid().ToString();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
            File.WriteAllText(StoragePath, id);
        }
        catch
        {
            // Fall back to hostname if we can't persist; not ideal but functional
            return Environment.MachineName;
        }

        return id;
    }
}
