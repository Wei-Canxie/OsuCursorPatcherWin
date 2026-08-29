using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;
using XamlBrush = Microsoft.UI.Xaml.Media.Brush;
using XamlSolidColorBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;

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
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, DwmWindowAttribute attribute, out int pvAttribute, int cbAttribute);

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
            bool success = false;

            // Method 1: DWM_SYSTEMBACKDROP_TYPE (Win11 22H2+)
            int backdropType = settings.BackgroundBlur switch
            {
                AppSettings.BlurMode.Mica => (int)DwmSystemBackdropType.DWMSBT_MAINWINDOW,
                AppSettings.BlurMode.Acrylic => (int)DwmSystemBackdropType.DWMSBT_TRANSIENTWINDOW,
                _ => (int)DwmSystemBackdropType.DWMSBT_NONE
            };

            int hr = DwmSetWindowAttribute(hwnd, DwmWindowAttribute.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));

            // Verify it actually took effect
            int verifyType = 0;
            int verifyHr = DwmGetWindowAttribute(hwnd, DwmWindowAttribute.DWMWA_SYSTEMBACKDROP_TYPE, out verifyType, sizeof(int));
            bool verified = verifyHr >= 0 && verifyType == backdropType;
            AppLog.Log($"DWM SystemBackdrop hr={hr}, type={backdropType}, verify={verified}, verifyHr={verifyHr}, verifyType={verifyType}");
            success = verified;

            // Method 2: SetWindowCompositionAttribute (undocumented Win10 method)
            if (!success)
            {
                success = TryAccentPolicyBlur(hwnd, settings);
            }

            if (success)
            {
                mainGrid.Background = new XamlSolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

                if (mainGrid.Children.Count > 1 && mainGrid.Children[1] is NavigationView nav)
                {
                    nav.Background = new XamlSolidColorBrush(Microsoft.UI.Colors.Transparent);
                }
                return;
            }

            // DWM failed, fallback to GDI+ blur on background image
            AppLog.Log("DWM methods failed, falling back to GDI+ Gaussian blur");
            ApplySolidBackground(mainGrid, settings, applyBlur: true);
            return;
        }

        DisableBlur(hwnd);
        ApplySolidBackground(mainGrid, settings, applyBlur: false);
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

    private static void ApplySolidBackground(Grid mainGrid, AppSettings settings, bool applyBlur)
    {
        XamlBrush? bg = null;

        if (!string.IsNullOrEmpty(settings.BackgroundImagePath) && File.Exists(settings.BackgroundImagePath))
        {
            try
            {
                using var bitmap = new Bitmap(settings.BackgroundImagePath);
                using var processed = applyBlur ? ApplyGaussianBlur(bitmap, settings.BackgroundBlurRadius) : new Bitmap(bitmap);
                var wb = new WriteableBitmap(processed.Width, processed.Height);
                using (var destStream = wb.PixelBuffer.AsStream())
                {
                    var pixels = new byte[processed.Width * processed.Height * 4];
                    BitmapData srcData = processed.LockBits(
                        new Rectangle(0, 0, processed.Width, processed.Height),
                        ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    Marshal.Copy(srcData.Scan0, pixels, 0, pixels.Length);
                    processed.UnlockBits(srcData);
                    destStream.Write(pixels, 0, pixels.Length);
                }
                bg = new ImageBrush
                {
                    ImageSource = wb,
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
            bg = new XamlSolidColorBrush(isDark
                ? Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E)
                : Windows.UI.Color.FromArgb(0xFF, 0xF0, 0xF0, 0xF0));
        }

        mainGrid.Background = bg;
    }

    private static Bitmap ApplyGaussianBlur(Bitmap source, int radius)
    {
        if (radius <= 0) return new Bitmap(source);
        radius = Math.Min(radius, 255);

        int size = radius * 2 + 1;
        double[] kernel = CreateGaussianKernel1D(size, radius);

        Bitmap output = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);

        BitmapData srcData = source.LockBits(
            new Rectangle(0, 0, source.Width, source.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        BitmapData dstData = output.LockBits(
            new Rectangle(0, 0, output.Width, output.Height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        int bytes = Math.Abs(srcData.Stride) * source.Height;
        byte[] srcBytes = new byte[bytes];
        byte[] dstBytes = new byte[bytes];
        Marshal.Copy(srcData.Scan0, srcBytes, 0, bytes);

        int half = radius;
        int width = source.Width;
        int height = source.Height;
        int stride = srcData.Stride;

        // Separable Gaussian blur
        byte[] tempBytes = new byte[bytes];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double r = 0, g = 0, b = 0, a = 0;
                for (int k = -half; k <= half; k++)
                {
                    int xx = Math.Min(Math.Max(x + k, 0), width - 1);
                    int idx = y * stride + xx * 4;
                    double weight = kernel[k + half];
                    b += srcBytes[idx] * weight;
                    g += srcBytes[idx + 1] * weight;
                    r += srcBytes[idx + 2] * weight;
                    a += srcBytes[idx + 3] * weight;
                }
                int dstIdx = y * stride + x * 4;
                tempBytes[dstIdx] = (byte)Math.Min(Math.Max(b, 0), 255);
                tempBytes[dstIdx + 1] = (byte)Math.Min(Math.Max(g, 0), 255);
                tempBytes[dstIdx + 2] = (byte)Math.Min(Math.Max(r, 0), 255);
                tempBytes[dstIdx + 3] = (byte)Math.Min(Math.Max(a, 0), 255);
            }
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double r = 0, g = 0, b = 0, a = 0;
                for (int k = -half; k <= half; k++)
                {
                    int yy = Math.Min(Math.Max(y + k, 0), height - 1);
                    int idx = yy * stride + x * 4;
                    double weight = kernel[k + half];
                    b += tempBytes[idx] * weight;
                    g += tempBytes[idx + 1] * weight;
                    r += tempBytes[idx + 2] * weight;
                    a += tempBytes[idx + 3] * weight;
                }
                int dstIdx = y * stride + x * 4;
                dstBytes[dstIdx] = (byte)Math.Min(Math.Max(b, 0), 255);
                dstBytes[dstIdx + 1] = (byte)Math.Min(Math.Max(g, 0), 255);
                dstBytes[dstIdx + 2] = (byte)Math.Min(Math.Max(r, 0), 255);
                dstBytes[dstIdx + 3] = (byte)Math.Min(Math.Max(a, 0), 255);
            }
        }

        Marshal.Copy(dstBytes, 0, dstData.Scan0, bytes);
        source.UnlockBits(srcData);
        output.UnlockBits(dstData);

        return output;
    }

    private static double[] CreateGaussianKernel1D(int size, double sigma)
    {
        double[] kernel = new double[size];
        double sum = 0;
        int half = size / 2;
        for (int i = 0; i < size; i++)
        {
            int x = i - half;
            kernel[i] = Math.Exp(-(x * x) / (2 * sigma * sigma));
            sum += kernel[i];
        }
        for (int i = 0; i < size; i++) kernel[i] /= sum;
        return kernel;
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
