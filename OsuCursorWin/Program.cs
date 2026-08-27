using System;
using System.IO;
using System.Threading;
using System.Windows;

using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace OsuCursorWin;

internal static class Program
{
    internal static string LogPath => Path.Combine(Path.GetTempPath(), "OsuCursorWin.log");

    internal static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging is best-effort and must never stop the cursor app.
        }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        var smoke = Array.IndexOf(args, "--smoke") >= 0;
        Log($"Starting OsuCursorWin smoke={smoke}");

        using var mutex = new Mutex(true, @"Local\OsuCursorWin.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Log("Another instance is already running.");
            return;
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            CursorReplacer.Restore();
            // Also restore the high-res timer so the system doesn't stay at 1ms
            // after an abnormal exit.
            try { NativeMethods.timeEndPeriod(1); } catch { }
        };

        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };

        app.SessionEnding += (_, _) => CursorReplacer.Restore();
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log($"Unhandled exception: {e.Exception}");
            CursorReplacer.Restore();
            e.Handled = true;
            if (smoke)
            {
                app.Shutdown();
            }
            else
            {
                MessageBox.Show(
                    e.Exception.Message,
                    "osu! Cursor",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };

        var window = new MainWindow(smoke);
        app.Run(window);
        Log("OsuCursorWin stopped.");
    }
}
