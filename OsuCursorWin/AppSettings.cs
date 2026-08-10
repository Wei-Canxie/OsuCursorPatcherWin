using System;
using System.IO;
using System.Text.Json;

namespace OsuCursorWin;

internal sealed class AppSettings
{
    private const double MinCursorWidth = 16.0;
    private const double MaxCursorWidth = 64.0;

    public double CursorWidth { get; set; } = 30.0;
    public bool AutoStart { get; set; }
    public bool TapSoundEnabled { get; set; } = true;
    public double TapSoundVolume { get; set; } = 1.0;
    public bool HoverSoundEnabled { get; set; } = true;
    public double HoverSoundVolume { get; set; } = 1.0;
    public bool HoverSoundAsResizePrompt { get; set; }

    internal static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OsuCursorWin",
            "settings.json");

    internal static bool Exists => File.Exists(SettingsPath);

    internal static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (settings is not null)
                {
                    settings.CursorWidth = Math.Clamp(settings.CursorWidth, MinCursorWidth, MaxCursorWidth);
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Program.Log($"Failed to load settings: {ex}");
        }

        return new AppSettings();
    }

    internal void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Program.Log($"Failed to save settings: {ex}");
        }
    }
}
