using System;
using System.IO;
using Microsoft.Win32;

namespace OsuCursorWin;

internal static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OsuCursorWin";

    internal static bool Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                var path = GetStartupPath();
                if (string.IsNullOrWhiteSpace(path))
                {
                    return false;
                }

                key.SetValue(ValueName, $"\"{path}\"");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Log($"Failed to update auto-start: {ex}");
            return false;
        }
    }

    private static string GetStartupPath()
    {
        var installedPath = @"C:\Program Files\OsuCursorWin\OsuCursorWin.exe";
        if (File.Exists(installedPath))
        {
            return installedPath;
        }

        return Environment.ProcessPath ?? string.Empty;
    }
}
