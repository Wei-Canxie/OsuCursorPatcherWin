using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OsuCursorWin;

/// <summary>
/// WinUI 3 Application. App.xaml declares the XamlControlsResources so the
/// WinUI Fluent control styles/templates are available. OnLaunched creates the
/// WinUI settings window, the native overlay window, and the system tray icon.
/// </summary>
public sealed partial class App : Application
{
    private SettingsWindow? _settingsWindow;
    private GdiCursorOverlay? _overlay;
    private TrayIcon? _trayIcon;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // Create the overlay window first (WinForms Form, GDI rendering)
            AppLog.Log("Creating overlay window...");
            _overlay = new GdiCursorOverlay();
            _overlay.Show();

            // Set up the system tray icon
            AppLog.Log("Setting up tray icon...");
            _trayIcon = new TrayIcon();
            _trayIcon.ShowSettingsRequested += ShowSettingsWindow;
            _trayIcon.ExitRequested += ExitApp;

            // Create the WinUI 3 settings window
            AppLog.Log("Creating SettingsWindow...");
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Activate();

            AppLog.Log("Application started: overlay + tray + WinUI3 settings window");
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
                _settingsWindow = new SettingsWindow();
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            }
            _settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            AppLog.Log($"ShowSettingsWindow failed: {ex}");
        }
    }

    private void ExitApp()
    {
        try
        {
            CursorReplacer.Restore();
            NativeMethods.timeEndPeriod(1);
            _trayIcon?.Dispose();
            _overlay?.Close();
            _settingsWindow?.Close();
        }
        catch (Exception ex)
        {
            AppLog.Log($"ExitApp failed: {ex}");
        }
        Environment.Exit(0);
    }
}