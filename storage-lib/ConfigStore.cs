using System.Text.Json;

namespace ScreenRecall.Storage;

/// <summary>Loads and saves <see cref="RecallConfig"/> atomically.</summary>
public static class ConfigStore
{
    /// <summary>Loads config from a path, creating defaults when the file is missing or unreadable.</summary>
    public static RecallConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new RecallConfig().Normalize();
        }

        try
        {
            string json = File.ReadAllText(path);
            RecallConfig? config = JsonSerializer.Deserialize<RecallConfig>(json, RecallJson.Options);
            return (config ?? new RecallConfig()).Normalize();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A broken config must never stop capture from running: fall back to defaults.
            return new RecallConfig().Normalize();
        }
    }

    /// <summary>Writes config atomically (temp file + rename), re-reading nothing.</summary>
    public static void Save(string path, RecallConfig config)
    {
        config.Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".part";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, RecallJson.Options));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Resolves the config path for a given runtime scope.</summary>
    public static string ResolvePath(bool perUser)
        => perUser ? RecallConfig.UserConfigPath() : RecallConfig.DefaultConfigPath();
}
