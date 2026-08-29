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
    // === Win32 APIs ===
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

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DWM_BLURBEHIND pBlurBehind);

    // Undocumented API for Windows 10 Acrylic/Blur
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowBlurBehind(IntPtr hwnd, ref DWM_BLURBEHIND pBlurBehind);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint LWA_COLORKEY = 0x00000001;

    private const int DWM_BB_ENABLE = 0x00000001;
    private const int DWM_BB_BLURREGION = 0x00000002;
    private const int DWM_BB_TRANSITIONONMAXIMIZED = 0x00000004;

    private enum DwmWindowAttribute
    {
        DWMWA_USE_IMMERSIVE_DARK_MODE = 20,
        DWMWA_SYSTEMBACKDROP_TYPE = 38,
        DWMWA_MICA_EFFECT = 1029,
        DWMWA_BLURBEHIND = 2
    }

    private enum DwmSystemBackdropType
    {
        DWMSBT_AUTO = 0,
        DWMSBT_NONE = 1,
        DWMSBT_MAINWINDOW = 2,
        DWMSBT_TRANSIENTWINDOW = 3,
        DWMSBT_TABBEDWINDOW = 4
    }

    // WindowCompositionAttribute undocumented enum
    private enum WindowCompositionAttribute
    {
        WCA_UNDEFINED = 0,
        WCA_NCRENDERING_ENABLED = 1,
        WCA_NCRENDERING_POLICY = 2,
        WCA_TRANSITIONS_FORCEDISABLED = 3,
        WCA_ALLOW_NCPAINT = 4,
        WCA_CAPTION_BUTTON_BOUNDS = 5,
        WCA_NONCLIENT_RTL_LAYOUT = 6,
        WCA_FORCE_ICONIC_REPRESENTATION = 7,
        WCA_EXTENDED_FRAME_BOUNDS = 8,
        WCA_HAS_ICONIC_BITMAP = 9,
        WCA_THEME_ATTRIBUTES = 10,
        WCA_NCRENDERING_EXEMPT = 11,
        WCA_NCADORNMENTINFO = 12,
        WCA_EXCLUDED_FROM_LIVEPREVIEW = 13,
        WCA_VIDEO_OVERLAY_ACTIVE = 14,
        WCA_FORCE_ACTIVE_APPEARANCE = 15,
        WCA_DISALLOW_PEEK = 16,
        WCA_CLOAK = 17,
        WCA_CLOAKED = 18,
        WCA_ACCENT_POLICY = 19,
        WCA_FREEZE_REPRESENTATION = 20,
        WCA_EVER_UNCLOAKED = 21,
        WCA_VISUAL_OWNER = 22,
        WCA_HOLOGRAPHIC = 23,
        WCA_EXCLUDED_FROM_DDA = 24,
        WCA_PASSIVEUPDATEMODE = 25,
        WCA_USEDARKMODECOLORS = 26,
        WCA_CORNER_STYLE = 27,
        WCA_COLOR_POLICY = 28,
        WCA_TRANSPARENT = 29,
        WCA_SYSTEMBACKDROP = 30,
        WCA_TYPE = 31
    };

    private enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
        ACCENT_ENABLE_HOSTBACKDROP = 5,
        ACCENT_INVALID_STATE = 6
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

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_BLURBEHIND
    {
        public uint dwFlags;
        public int fEnable;
        public IntPtr hRgnBlur;
        public int fTransitionOnMaximized;
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

            // Method 1: Undocumented SetWindowCompositionAttribute (most reliable on Win10/11)
            if (!success)
            {
                success = TryWindowCompositionBlur(hwnd, settings);
            }

            // Method 2: DWM_BLURBEHIND (Windows 10/11 compatible)
            if (!success)
            {
                success = TryDwmBlurBehind(hwnd, settings);
            }

            // Method 3: DWM_SYSTEMBACKDROP_TYPE (Windows 11 22H2+)
            if (!success)
            {
                success = TryDwmSystemBackdrop(hwnd, settings);
            }

            if (success)
            {
                mainGrid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                return;
            }

            AppLog.Log("All blur methods failed");
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

            // Gradient color: BGRA format, alpha controls intensity
            int alpha = (int)(settings.BackgroundBlurRadius * 255.0 / 1024.0);
            byte r = 0xB0, g = 0xB0, b = 0xB0;
            accent.GradientColor = (alpha << 24) | (b << 16) | (g << 8) | r;
            accent.AccentFlags = 0x20 | 0x40 | 0x80 | 0x100; // Enable gradient + blur

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
            AppLog.Log($"SetWindowCompositionAttribute result: {hr}");
            return hr == 0 || hr == 1;
        }
        catch (Exception ex)
        {
            AppLog.Log($"WindowComposition blur failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryDwmBlurBehind(IntPtr hwnd, AppSettings settings)
    {
        try
        {
            var bb = new DWM_BLURBEHIND
            {
                dwFlags = DWM_BB_ENABLE | DWM_BB_BLURREGION,
                fEnable = 1,
                hRgnBlur = IntPtr.Zero, // null = entire window
                fTransitionOnMaximized = 0
            };

            int hr = DwmEnableBlurBehindWindow(hwnd, ref bb);
            AppLog.Log($"DwmEnableBlurBehindWindow result: {hr}");
            return hr >= 0;
        }
        catch (Exception ex)
        {
            AppLog.Log($"DWM_BLURBEHIND failed: {ex.Message}");
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
            AppLog.Log($"DWMWA_SYSTEMBACKDROP_TYPE result: {hr}");
            return hr >= 0;
        }
        catch (Exception ex)
        {
            AppLog.Log($"DWM SystemBackdrop failed: {ex.Message}");
            return false;
        }
    }

    private static void DisableAllBlur(IntPtr hwnd)
    {
        // Disable DWM_BLURBEHIND
        var bb = new DWM_BLURBEHIND
        {
            dwFlags = DWM_BB_ENABLE,
            fEnable = 0,
            hRgnBlur = IntPtr.Zero,
            fTransitionOnMaximized = 0
        };
        DwmEnableBlurBehindWindow(hwnd, ref bb);

        // Disable SetWindowCompositionAttribute
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
