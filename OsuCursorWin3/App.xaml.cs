using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OsuCursorWin;

/// <summary>
/// WinUI 3 Application. App.xaml declares the XamlControlsResources.
/// OnLaunched starts the overlay + rendering engine, settings window, and tray.
/// </summary>
public sealed partial class App : Application
{
    private SettingsWindow? _settingsWindow;
    private GdiCursorOverlay? _overlay;
    private TrayIcon? _trayIcon;
    private CursorEngine? _engine;
    private TapSoundPlayer? _tapSoundPlayer;
    private TapSoundPlayer? _hoverSoundPlayer;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // Create sound players
            AppLog.Log("Creating sound players...");
            _tapSoundPlayer = new TapSoundPlayer(LoadResourceBytes("OsuCursorWin.Audio.cursorTap.wav"));
            _hoverSoundPlayer = new TapSoundPlayer(LoadResourceBytes("OsuCursorWin.Audio.defaultHover.wav"));

            // Create the overlay window (WinForms Form, GDI rendering)
            AppLog.Log("Creating overlay window...");
            _overlay = new GdiCursorOverlay();
            _overlay.ShowOverlay();

            // Set up the system tray icon
            AppLog.Log("Setting up tray icon...");
            _trayIcon = new TrayIcon();
            _trayIcon.ShowSettingsRequested += ShowSettingsWindow;
            _trayIcon.ExitRequested += ExitApp;

            // Create and start the rendering engine
            AppLog.Log("Creating CursorEngine...");
            _engine = new CursorEngine(AppSettings.Load(), _overlay,
                _tapSoundPlayer, _hoverSoundPlayer);
            _engine.Start();

            // Create the WinUI 3 settings window
            AppLog.Log("Creating SettingsWindow...");
            _settingsWindow = new SettingsWindow(_engine);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Activate();

            AppLog.Log("Application started: engine + tray + WinUI3 settings window");
        }
        catch (Exception ex)
        {
            AppLog.Log($"App.OnLaunched threw: {ex}");
            throw;
        }
    }

    private void ShowSettingsWindow()
    {
        try
        {
            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow(_engine);
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            }
            // Window may have been hidden (not closed) — bring it back.
            _settingsWindow.AppWindow.Show();
            _settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            AppLog.Log($"ShowSettingsWindow failed: {ex}");
        }
    }

    private static byte[] LoadResourceBytes(string resourceName)
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing resource: {resourceName}");
        using var buffer = new System.IO.MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private void ExitApp()
    {
        try
        {
            _engine?.Dispose();
            _trayIcon?.Dispose();
            _overlay?.Dispose();
            _tapSoundPlayer?.Dispose();
            _hoverSoundPlayer?.Dispose();
            _settingsWindow?.Close();
        }
        catch (Exception ex)
        {
            AppLog.Log($"ExitApp failed: {ex}");
        }
        Environment.Exit(0);
    }
}