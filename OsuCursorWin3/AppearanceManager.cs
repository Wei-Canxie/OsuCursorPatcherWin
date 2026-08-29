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

    // SetWindowCompositionAttribute (undocumented)
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

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

    private enum WindowCompositionAttribute
    {
        WCA_ACCENT_POLICY = 19
    }

    private enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
        ACCENT_ENABLE_HOSTBACKDROP = 5
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
        AppLog.Log($"DWM composition: {dwmEnabled}, blur: {settings.BackgroundBlur}, radius: {settings.BackgroundBlurRadius}");

        if (useBlur)
        {
            if (dwmEnabled == 0)
            {
                AppLog.Log("DWM composition disabled");
                ApplySolidBackground(mainGrid, settings);
                return;
            }

            bool success = false;

            // Method 1: SetWindowCompositionAttribute (classic Win10 method, broken on Win11 24H2)
            if (!success)
            {
                success = TryWindowCompositionBlur(hwnd, settings);
                AppLog.Log($"Method 1 (WindowComposition): {success}");
            }

            // Method 2: DWM_SYSTEMBACKDROP_TYPE (Win11 22H2+)
            if (!success)
            {
                success = TryDwmSystemBackdrop(hwnd, settings);
                AppLog.Log($"Method 2 (SystemBackdropType): {success}");
            }

            // Method 3: DWMWA_MICA_EFFECT (Win10 / early Win11)
            if (!success)
            {
                success = TryDwmMicaEffect(hwnd, settings);
                AppLog.Log($"Method 3 (MicaEffect): {success}");
            }

            // Method 4: DwmExtendFrameIntoClientArea + DWM_BLURBEHIND region
            if (!success)
            {
                success = TryDwmBlurBehind(hwnd, settings);
                AppLog.Log($"Method 4 (BlurBehind): {success}");
            }

            if (success)
            {
                mainGrid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                AppLog.Log($"Blur applied with radius={settings.BackgroundBlurRadius}");
                return;
            }

            AppLog.Log("ALL blur methods failed");
        }

        // Disable blur
        DisableAllBlur(hwnd);
        ApplySolidBackground(mainGrid, settings);
    }

    private static bool TryWindowCompositionBlur(IntPtr hwnd, AppSettings settings)
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

            // GradientColor: BGRA format
            // For Mica: use a neutral gray with alpha based on radius
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
            AppLog.Log($"  WindowComposition hr={hr}, accent={accent.AccentState}, color=0x{accent.GradientColor:X8}");
            return hr == 0 || hr == 1;
        }
        catch (Exception ex)
        {
            AppLog.Log($"  WindowComposition error: {ex.Message}");
            return false;
        }
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
            AppLog.Log($"  SystemBackdrop hr={hr}, type={backdropType}");
            return hr >= 0;
        }
        catch (Exception ex)
        {
            AppLog.Log($"  SystemBackdrop error: {ex.Message}");
            return false;
        }
    }

    private static bool TryDwmMicaEffect(IntPtr hwnd, AppSettings settings)
    {
        try
        {
            int mica = settings.BackgroundBlur == AppSettings.BlurMode.Mica ? 2 : 0;
            int hr = DwmSetWindowAttribute(hwnd, DwmWindowAttribute.DWMWA_MICA_EFFECT, ref mica, sizeof(int));
            AppLog.Log($"  MicaEffect hr={hr}, mica={mica}");

            if (hr >= 0)
            {
                var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);
            }

            return hr >= 0;
        }
        catch (Exception ex)
        {
            AppLog.Log($"  MicaEffect error: {ex.Message}");
            return false;
        }
    }

    private static bool TryDwmBlurBehind(IntPtr hwnd, AppSettings settings)
    {
        try
        {
            var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            int hr1 = DwmExtendFrameIntoClientArea(hwnd, ref margins);
            AppLog.Log($"  BlurBehind margins hr={hr1}");
            return hr1 >= 0;
        }
        catch (Exception ex)
        {
            AppLog.Log($"  BlurBehind error: {ex.Message}");
            return false;
        }
    }

    private static void DisableAllBlur(IntPtr hwnd)
    {
        // Disable WindowComposition
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

        // Disable DWM_SYSTEMBACKDROP
        int noneType = (int)DwmSystemBackdropType.DWMSBT_NONE;
        DwmSetWindowAttribute(hwnd, DwmWindowAttribute.DWMWA_SYSTEMBACKDROP_TYPE, ref noneType, sizeof(int));

        // Disable MICA_EFFECT
        int mica = 0;
        DwmSetWindowAttribute(hwnd, DwmWindowAttribute.DWMWA_MICA_EFFECT, ref mica, sizeof(int));

        // Collapse margins
        var noMargins = new MARGINS { cxLeftWidth = 0, cxRightWidth = 0, cyTopHeight = 0, cyBottomHeight = 0 };
        DwmExtendFrameIntoClientArea(hwnd, ref noMargins);
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
