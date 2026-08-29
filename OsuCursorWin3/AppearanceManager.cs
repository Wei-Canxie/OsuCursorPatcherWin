using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;

namespace OsuCursorWin;

internal static class AppearanceManager
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, DwmWindowAttribute attribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmIsCompositionEnabled(ref int pfEnabled);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;

    private enum DwmWindowAttribute
    {
        DWMWA_SYSTEMBACKDROP_TYPE = 38
    }

    private enum DwmSystemBackdropType
    {
        DWMSBT_NONE = 1,
        DWMSBT_MAINWINDOW = 2,
        DWMSBT_TRANSIENTWINDOW = 3
    }

    private enum WindowCompositionAttribute
    {
        WCA_ACCENT_POLICY = 19
    }

    private enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    public static void ApplyAll(Window window, AppSettings settings)
    {
        ApplyTheme(window, settings);
        ApplyBackground(window, settings);
        ApplyOpacity(window, settings);
    }

    public static void ApplyTheme(Window window, AppSettings settings)
    {
        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = settings.Theme switch
            {
                AppSettings.ThemeMode.Light => ElementTheme.Light,
                AppSettings.ThemeMode.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
    }

    public static void ApplyOpacity(Window window, AppSettings settings)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

        if (settings.BackgroundBlur != AppSettings.BlurMode.Default)
        {
            if ((exStyle & WS_EX_LAYERED) != 0)
            {
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle & ~WS_EX_LAYERED);
            }
            return;
        }

        try
        {
            if ((exStyle & WS_EX_LAYERED) == 0)
            {
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
            }
            var alpha = (byte)Math.Clamp(settings.WindowOpacity * 255, 76, 255);
            SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
        }
        catch (Exception ex)
        {
            AppLog.Log($"ApplyOpacity failed: {ex.Message}");
        }
    }

    public static void ApplyBackground(Window window, AppSettings settings)
    {
        if (window.Content is not Grid mainGrid) return;

        var hwnd = WindowNative.GetWindowHandle(window);
        bool useBlur = settings.BackgroundBlur != AppSettings.BlurMode.Default;

        int dwmEnabled = 0;
        DwmIsCompositionEnabled(ref dwmEnabled);
        AppLog.Log($"DWM: {dwmEnabled}, blur: {settings.BackgroundBlur}, radius: {settings.BackgroundBlurRadius}");

        if (useBlur && dwmEnabled != 0)
        {
            bool success = TryDwmSystemBackdrop(hwnd, settings);

            if (!success)
            {
                success = TryAccentPolicyBlur(hwnd, settings);
            }

            if (success)
            {
                mainGrid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

                // Make NavigationView transparent so backdrop shows
                if (mainGrid.Children.Count > 1 && mainGrid.Children[1] is NavigationView nav)
                {
                    nav.Background = new SolidColorBrush(Colors.Transparent);
                }
                return;
            }
        }

        DisableBlur(hwnd);
        ApplySolidBackground(mainGrid, settings);
    }

    private static bool TryDwmSystemBackdrop(IntPtr hwnd, AppSettings settings)
    {
        try
        {
            int backdropType = settings.BackgroundBlur switch
            {
                AppSettings.BlurMode.Mica => (int)DwmSystemBackdropType.DWMSBT_MAINWINDOW,
                AppSettings.BlurMode.Acrylic => (int)DwmSystemBackdropType.DWMSBT_TRANSIENTWINDOW,
                _ => (int)DwmSystemBackdropType.DWMSBT_NONE
            };

            int hr = DwmSetWindowAttribute(hwnd, DwmWindowAttribute.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
            AppLog.Log($"DWM SystemBackdrop hr={hr}, type={backdropType}");
            return hr >= 0;
        }
        catch (Exception ex)
        {
            AppLog.Log($"DWM SystemBackdrop error: {ex.Message}");
            return false;
        }
    }

    private static bool TryAccentPolicyBlur(IntPtr hwnd, AppSettings settings)
    {
        try
        {
            var accent = new AccentPolicy();
            accent.AccentState = settings.BackgroundBlur switch
            {
                AppSettings.BlurMode.Acrylic => AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AppSettings.BlurMode.Mica => AccentState.ACCENT_ENABLE_BLURBEHIND,
                _ => AccentState.ACCENT_DISABLED
            };

            int alpha = Math.Clamp((int)(settings.BackgroundBlurRadius * 255.0 / 1024.0), 0, 255);
            accent.GradientColor = (alpha << 24) | 0x00B0B0B0;
            accent.AccentFlags = 0x20 | 0x40 | 0x80 | 0x100;

            int accentSize = Marshal.SizeOf(accent);
            IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);
            Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                Data = accentPtr,
                SizeOfData = accentSize
            };

            int hr = SetWindowCompositionAttribute(hwnd, ref data);
            Marshal.FreeHGlobal(accentPtr);
            AppLog.Log($"AccentPolicy hr={hr}");
            return hr == 0 || hr == 1;
        }
        catch (Exception ex)
        {
            AppLog.Log($"AccentPolicy error: {ex.Message}");
            return false;
        }
    }

    private static void DisableBlur(IntPtr hwnd)
    {
        var accent = new AccentPolicy { AccentState = AccentState.ACCENT_DISABLED };
        int accentSize = Marshal.SizeOf(accent);
        IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);
        Marshal.StructureToPtr(accent, accentPtr, false);

        var data = new WindowCompositionAttributeData
        {
            Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
            Data = accentPtr,
            SizeOfData = accentSize
        };
        SetWindowCompositionAttribute(hwnd, ref data);
        Marshal.FreeHGlobal(accentPtr);

        int noneType = (int)DwmSystemBackdropType.DWMSBT_NONE;
        DwmSetWindowAttribute(hwnd, DwmWindowAttribute.DWMWA_SYSTEMBACKDROP_TYPE, ref noneType, sizeof(int));
    }

    private static void ApplySolidBackground(Grid mainGrid, AppSettings settings)
    {
        Brush? bg = null;

        if (!string.IsNullOrEmpty(settings.BackgroundImagePath) && File.Exists(settings.BackgroundImagePath))
        {
            try
            {
                var bitmap = new BitmapImage();
                using var stream = File.OpenRead(settings.BackgroundImagePath);
                bitmap.SetSource(stream.AsRandomAccessStream());
                bg = new ImageBrush
                {
                    ImageSource = bitmap,
                    Stretch = Stretch.UniformToFill,
                    Opacity = Math.Clamp(settings.BackgroundImageOpacity, 0, 1)
                };
            }
            catch (Exception ex)
            {
                AppLog.Log($"Background image failed: {ex.Message}");
            }
        }

        if (bg == null)
        {
            var isDark = settings.Theme == AppSettings.ThemeMode.Dark ||
                         (settings.Theme == AppSettings.ThemeMode.FollowSystem && IsSystemDark());
            bg = new SolidColorBrush(isDark
                ? Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E)
                : Windows.UI.Color.FromArgb(0xFF, 0xF0, 0xF0, 0xF0));
        }

        mainGrid.Background = bg;
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int i && i == 0;
        }
        catch { return false; }
    }

    public static void Cleanup() { }
}
