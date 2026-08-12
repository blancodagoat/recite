using System.Text.Json;
using System.Text.Json.Serialization;

namespace Recite;

/// <summary>The on-disk shape of config.json.</summary>
internal sealed class ConfigFile
{
    public string? GrabHotkey { get; set; }
    public bool? ExperimentalOneOcr { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConfigFile))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext
{
}

internal sealed class AppConfig
{
    public HotkeyBinding GrabHotkey { get; set; } = HotkeyBinding.DefaultGrab;

    /// <summary>Opt-in to the Windows 11 Snipping Tool OCR model. Off until its ABI is
    /// finished; see <see cref="OneOcr"/>.</summary>
    public bool ExperimentalOneOcr { get; set; }

    /// <summary>True when no config existed on disk — the app's very first launch.</summary>
    public bool FirstRun { get; private set; }

    /// <summary>
    /// Never throws. Anything unreadable or malformed collapses to defaults, and the
    /// file is rewritten so the next launch starts from something valid.
    /// </summary>
    public static AppConfig Load()
    {
        var config = new AppConfig();
        bool rewrite = true;

        try
        {
            config.FirstRun = !File.Exists(AppInfo.ConfigPath);
            if (!config.FirstRun)
            {
                var json = File.ReadAllText(AppInfo.ConfigPath);
                var file = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.ConfigFile);
                if (file is not null && HotkeyBinding.TryParse(file.GrabHotkey, out var hotkey))
                {
                    config.GrabHotkey = hotkey;
                    config.ExperimentalOneOcr = file.ExperimentalOneOcr ?? false;
                    rewrite = file.ExperimentalOneOcr is null;
                }
            }
        }
        catch
        {
            // Defaults it is.
        }

        if (rewrite)
        {
            config.Save();
        }

        return config;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDirectory);
            var file = new ConfigFile { GrabHotkey = GrabHotkey.ToString(), ExperimentalOneOcr = ExperimentalOneOcr };
            File.WriteAllText(
                AppInfo.ConfigPath, JsonSerializer.Serialize(file, ConfigJsonContext.Default.ConfigFile));
        }
        catch
        {
            // A failed save costs the user their settings on next launch, nothing worse.
        }
    }
}
