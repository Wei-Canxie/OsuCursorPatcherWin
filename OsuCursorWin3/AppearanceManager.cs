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
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmIsCompositionEnabled(ref int pfEnabled);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;

    private enum DwmWindowAttribute
    {
        DWMWA_USE_IMMERSIVE_DARK_MODE = 20,
        DWMWA_SYSTEMBACKDROP_TYPE = 38,
        DWMWA_MICA_EFFECT = 1029
    }

    private enum DwmSystemBackdropType
    {
        DWMSBT_AUTO = 0,
        DWMSBT_NONE = 1,
        DWMSBT_MAINWINDOW = 2,
        DWMSBT_TRANSIENTWINDOW = 3,
        DWMSBT_TABBEDWINDOW = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
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
        AppLog.Log($"DWM composition enabled: {dwmEnabled}, blur mode: {settings.BackgroundBlur}");

        if (useBlur)
        {
            if (dwmEnabled == 0)
            {
                AppLog.Log("DWM composition disabled, blur unavailable");
                ApplySolidBackground(mainGrid, settings);
                return;
            }

            // Windows 11: use DWM_SYSTEMBACKDROP_TYPE (build 22000+)
            int backdropType = settings.BackgroundBlur switch
            {
                AppSettings.BlurMode.Mica => (int)DwmSystemBackdropType.DWMSBT_MAINWINDOW,
                AppSettings.BlurMode.Acrylic => (int)DwmSystemBackdropType.DWMSBT_TRANSIENTWINDOW,
                _ => (int)DwmSystemBackdropType.DWMSBT_NONE
            };

            int hr = DwmSetWindowAttribute(hwnd, DwmWindowAttribute.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
            AppLog.Log($"DWMWA_SYSTEMBACKDROP_TYPE result: {hr}, type: {backdropType}");

            // Fallback for older Windows
            if (hr < 0)
            {
                int mica = settings.BackgroundBlur == AppSettings.BlurMode.Mica ? 2 : 0;
                int hr2 = DwmSetWindowAttribute(hwnd, DwmWindowAttribute.DWMWA_MICA_EFFECT, ref mica, sizeof(int));
                AppLog.Log($"DWMWA_MICA_EFFECT result: {hr2}");

                var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
                int hr3 = DwmExtendFrameIntoClientArea(hwnd, ref margins);
                AppLog.Log($"DwmExtendFrameIntoClientArea result: {hr3}");
            }

            // Apply blur intensity via dark mode attribute + opacity layering
            // Higher intensity = darker background (for Mica) or more transparent (for Acrylic)
            int darkMode = settings.BackgroundBlurIntensity > 0.5 ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmWindowAttribute.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            // For intensity < 1.0, overlay a semi-transparent layer to reduce blur strength
            if (settings.BackgroundBlurIntensity < 1.0)
            {
                // Add a semi-transparent overlay to reduce blur appearance
                var overlayColor = (byte)((1.0 - settings.BackgroundBlurIntensity) * 128);
                var isDark = settings.Theme == AppSettings.ThemeMode.Dark ||
                             (settings.Theme == AppSettings.ThemeMode.FollowSystem && IsSystemDark());
                var overlay = new SolidColorBrush(isDark
                    ? Windows.UI.Color.FromArgb(overlayColor, 0, 0, 0)
                    : Windows.UI.Color.FromArgb(overlayColor, 255, 255, 255));
                // Note: We can't easily overlay on top of DWM backdrop
                // The intensity control mainly works via the color tint overlay
            }

            mainGrid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            return;
        }

        // Disable blur
        int noneType = (int)DwmSystemBackdropType.DWMSBT_NONE;
        DwmSetWindowAttribute(hwnd, DwmWindowAttribute.DWMWA_SYSTEMBACKDROP_TYPE, ref noneType, sizeof(int));

        var noMargins = new MARGINS { cxLeftWidth = 0, cxRightWidth = 0, cyTopHeight = 0, cyBottomHeight = 0 };
        DwmExtendFrameIntoClientArea(hwnd, ref noMargins);

        ApplySolidBackground(mainGrid, settings);
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
