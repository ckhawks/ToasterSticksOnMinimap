using System.IO;
using System.Text.Json;

namespace ToasterSticksOnMap;

public class ModSettings
{
    public bool enablePuckHeightOpacity { get; set; } = true;
    public bool enableSideViews { get; set; } = false;
    public bool enableFogOfWar { get; set; } = false;

    public float stickMaxHeight { get; set; } = 3f;
    public float stickMinScale { get; set; } = 0.5f;

    public float puckMaxHeight { get; set; } = 8f;
    public float puckMinScale { get; set; } = 0.6f;

    public float fogFovDegrees { get; set; } = 120f;
    public float fogHiddenOpacity { get; set; } = 0f;

    static string ConfigurationFileName = $"{Plugin.MOD_NAME}.json";

    public static ModSettings Load()
    {
        Plugin.Log($"Loading {ConfigurationFileName}...");
        var path = GetConfigPath();
        var dir = Path.GetDirectoryName(path);

        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            Plugin.Log($"Created missing /config directory");
        }

        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<ModSettings>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return settings ?? new ModSettings();
            }
            catch (JsonException je)
            {
                Plugin.Log($"Corrupt config JSON, using defaults: {je.Message}");
                return new ModSettings();
            }
        }

        var defaults = new ModSettings();
        File.WriteAllText(path,
            JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true }));

        Plugin.Log($"Config file `{path}` did not exist, created with defaults.");
        return defaults;
    }

    public void Save()
    {
        var path = GetConfigPath();
        var dir = Path.GetDirectoryName(path);

        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string GetConfigPath()
    {
        string rootPath = Path.GetFullPath(".");
        return Path.Combine(rootPath, "config", ConfigurationFileName);
    }
}
