using System;
using System.IO;
using System.Text.Json;

namespace OsuCursorWin;

internal sealed class AppSettings
{
    private const double MinCursorWidth = 16.0;
    private const double MaxCursorWidth = 64.0;

    public double CursorWidth { get; set; } = 32.0;
    public bool AutoStart { get; set; }
    public bool TapSoundEnabled { get; set; } = true;
    public double TapSoundVolume { get; set; } = 1.0;
    public bool HoverSoundEnabled { get; set; } = true;
    public double HoverSoundVolume { get; set; } = 1.0;
    public bool HoverSoundAsResizePrompt { get; set; }

    /// <summary>Theme mode: follow system, light, or dark.</summary>
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    /// <summary>Settings window width in pixels.</summary>
    public double WindowWidth { get; set; } = 960.0;
    /// <summary>Settings window height in pixels.</summary>
    public double WindowHeight { get; set; } = 680.0;
    /// <summary>Window content opacity. 0.3 – 1.0.</summary>
    public double WindowOpacity { get; set; } = 0.9;
    /// <summary>Background image file path. Empty = none.</summary>
    public string BackgroundImagePath { get; set; } = DefaultBackgroundPath;

    /// <summary>Default background image shipped with the app.</summary>
    internal static string DefaultBackgroundPath =>
        Path.Combine(AppContext.BaseDirectory, "background-default.jpg");
    /// <summary>Background image opacity. 0.0 – 1.0.</summary>
    public double BackgroundImageOpacity { get; set; } = 0.8;
    /// <summary>Background blur type: default (solid), Mica, Acrylic.</summary>
    public BlurMode BackgroundBlur { get; set; } = BlurMode.Default;
    /// <summary>Background blur radius in pixels. 0 – 255.</summary>
    public int BackgroundBlurRadius { get; set; } = 8;

    public enum ThemeMode { FollowSystem, Light, Dark }
    public enum BlurMode { Default, Mica, Acrylic }

    // ---- Req 2a: per-scene cursor geometry tuning ----
    // Normal scene = the animated GDI overlay (visible over normal windows).
    // DC scene    = the static osu system cursor (over Start menu, Action
    //               Center, volume/clipboard flyouts, and special states).
    // Each can be tuned independently for size, aspect ratio, and hotspot
    // offset so the two scenes can be made to match.

    /// <summary>Normal-scene (overlay) size multiplier relative to CursorWidth. 1.0 = default.</summary>
    public double NormalSize { get; set; } = 1.0;
    /// <summary>Normal-scene horizontal aspect multiplier. 1.0 = native image aspect.</summary>
    public double NormalAspectX { get; set; } = 1.0;
    /// <summary>Normal-scene vertical aspect multiplier. 1.0 = native image aspect.</summary>
    public double NormalAspectY { get; set; } = 1.0;
    /// <summary>Normal-scene hotspot X offset from the tuned anchor, in physical px.</summary>
    public double NormalHotspotX { get; set; } = 0.0;
    /// <summary>Normal-scene hotspot Y offset from the tuned anchor, in physical px.</summary>
    public double NormalHotspotY { get; set; } = 0.0;

    /// <summary>DC-scene system-cursor size in px (per-CURSOR bitmap edge). 0 = auto (follows CursorWidth).</summary>
    public double DcCursorSize { get; set; } = 0.0;
    /// <summary>DC-scene horizontal aspect multiplier. 1.0 = native image aspect.</summary>
    public double DcAspectX { get; set; } = 1.0;
    /// <summary>DC-scene vertical aspect multiplier. 1.0 = native image aspect.</summary>
    public double DcAspectY { get; set; } = 1.0;
    /// <summary>DC-scene hotspot X offset from default, in physical px.</summary>
    public double DcHotspotX { get; set; } = 0.0;
    /// <summary>DC-scene hotspot Y offset from default, in physical px.</summary>
    public double DcHotspotY { get; set; } = 0.0;

    internal static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OsuCursorPatcherWin",
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
                    // Fall back to the shipped default image whenever the
                    // configured path is empty or the file went away, so the
                    // window never silently degrades to a blank background.
                    if (string.IsNullOrEmpty(settings.BackgroundImagePath)
                        || !File.Exists(settings.BackgroundImagePath))
                    {
                        settings.BackgroundImagePath = DefaultBackgroundPath;
                    }
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Log($"Failed to load settings: {ex}");
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
            AppLog.Log($"Failed to save settings: {ex}");
        }
    }
}
