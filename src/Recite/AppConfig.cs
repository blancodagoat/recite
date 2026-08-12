using System.Text.Json;
using System.Text.Json.Serialization;

namespace Recite;

/// <summary>The on-disk shape of config.json.</summary>
internal sealed class ConfigFile
{
    public string? GrabHotkey { get; set; }
    public bool? UseWindows11Ocr { get; set; }
    public bool? UpdateNotify { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConfigFile))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext
{
}

internal sealed class AppConfig
{
    public HotkeyBinding GrabHotkey { get; set; } = HotkeyBinding.DefaultGrab;

    /// <summary>Use the sharper Windows 11 OCR model when its package is present. On by
    /// default; set false to force the built-in engine. See <see cref="OneOcr"/>.</summary>
    public bool UseWindows11Ocr { get; set; } = true;

    /// <summary>True when no config existed on disk — the app's very first launch.</summary>
    public bool FirstRun { get; private set; }

    /// <summary>Opt-in background update notifications. Off = never phones home.</summary>
    public bool UpdateNotify { get; set; }

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
                    config.UseWindows11Ocr = file.UseWindows11Ocr ?? true;
                    config.UpdateNotify = file.UpdateNotify ?? false;
                    rewrite = file.UseWindows11Ocr is null || file.UpdateNotify is null;
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
            var file = new ConfigFile
            {
                GrabHotkey = GrabHotkey.ToString(),
                UseWindows11Ocr = UseWindows11Ocr,
                UpdateNotify = UpdateNotify,
            };
            File.WriteAllText(
                AppInfo.ConfigPath, JsonSerializer.Serialize(file, ConfigJsonContext.Default.ConfigFile));
        }
        catch
        {
            // A failed save costs the user their settings on next launch, nothing worse.
        }
    }
}
