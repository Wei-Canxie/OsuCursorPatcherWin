using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT;
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

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;

    private static MicaController? _micaController;
    private static DesktopAcrylicController? _acrylicController;
    private static SystemBackdropConfiguration? _backdropConfig;

    public static void ApplyAll(Window window, AppSettings settings)
    {
        ApplyTheme(window, settings);
        ApplyBackground(window, settings);
        ApplyOpacity(window, settings);
    }

    /// <summary>
    /// Async version of ApplyAll that processes background blur on a background thread
    /// to avoid blocking the UI thread when applying Gaussian blur.
    /// </summary>
    public static async Task ApplyAllAsync(Window window, AppSettings settings)
    {
        ApplyTheme(window, settings);
        // Apply opacity BEFORE backdrop so WS_EX_LAYERED is removed first
        ApplyOpacity(window, settings);
        await ApplyBackgroundAsync(window, settings);
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

        bool useCompositionBackdrop = settings.BackgroundBlur != AppSettings.BlurMode.Default;

        if (useCompositionBackdrop)
        {
            // Composition backdrop (Mica/Acrylic) requires transparent window —
            // WS_EX_LAYERED conflicts with it, so remove the layered style
            if ((exStyle & WS_EX_LAYERED) != 0)
            {
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle & ~WS_EX_LAYERED);
            }
        }
        else
        {
            // Default mode: use WS_EX_LAYERED for opacity
            if ((exStyle & WS_EX_LAYERED) == 0)
            {
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
            }
            var alpha = (byte)Math.Clamp(settings.WindowOpacity * 255, 25, 255);
            SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
        }
    }

    public static void ApplyBackground(Window window, AppSettings settings)
    {
        if (window.Content is not Grid mainGrid) return;

        bool useBlur = settings.BackgroundBlur != AppSettings.BlurMode.Default;
        AppLog.Log($"ApplyBackground blur={settings.BackgroundBlur}, radius={settings.BackgroundBlurRadius}");

        if (useBlur)
        {
            // Mica/Acrylic: Composition backdrop handles the visual, no image blur applied
            mainGrid.Background = new XamlSolidColorBrush(Colors.Transparent);

            if (TrySetCompositionBackdrop(window, settings))
            {
                AppLog.Log("WASDK 2.4 Composition backdrop applied");
                return;
            }

            // Fallback to GDI+ Gaussian blur
            AppLog.Log("Composition backdrop failed, falling back to GDI+ blur");
            ApplyBlurBackground(mainGrid, settings);
        }
        else
        {
            // Default mode: blur radius controls background image blur
            ClearBackdrop(window);
            ApplySolidBackground(mainGrid, settings, applyBlur: true);
        }
    }

    /// <summary>
    /// Async version of ApplyBackground that runs GDI+ blur on a background thread
    /// to avoid blocking the UI thread.
    /// </summary>
    public static async Task ApplyBackgroundAsync(Window window, AppSettings settings)
    {
        if (window.Content is not Grid mainGrid) return;

        bool useBlur = settings.BackgroundBlur != AppSettings.BlurMode.Default;
        AppLog.Log($"ApplyBackgroundAsync blur={settings.BackgroundBlur}, radius={settings.BackgroundBlurRadius}");

        if (useBlur)
        {
            // Mica/Acrylic: Composition backdrop handles the visual
            mainGrid.Background = new XamlSolidColorBrush(Colors.Transparent);

            if (TrySetCompositionBackdrop(window, settings))
            {
                AppLog.Log("WASDK 2.4 Composition backdrop applied");
                return;
            }

            AppLog.Log("Composition backdrop failed, falling back to GDI+ blur (async)");
            await ApplyBlurBackgroundAsync(mainGrid, settings);
        }
        else
        {
            // Default mode: blur radius controls background image blur
            ClearBackdrop(window);
            await ApplySolidBackgroundAsync(mainGrid, settings, applyBlur: true);
        }
    }

    /// <summary>
    /// Async version of ApplyBlurBackground that processes the image on a background thread.
    /// </summary>
    private static async Task ApplyBlurBackgroundAsync(Grid mainGrid, AppSettings settings)
    {
        if (string.IsNullOrEmpty(settings.BackgroundImagePath) || !File.Exists(settings.BackgroundImagePath))
            return;

        try
        {
            var path = settings.BackgroundImagePath;
            var radius = Math.Clamp(settings.BackgroundBlurRadius, 0, 255);
            var opacity = Math.Clamp(settings.BackgroundImageOpacity, 0, 1);
            var blurMode = settings.BackgroundBlur;

            // Heavy GDI+ work on background thread
            var processedBitmap = await Task.Run(() =>
            {
                using var bitmap = new Bitmap(path);
                using var blurred = ApplyGaussianBlur(bitmap, radius);
                ApplyOverlayTint(blurred, blurMode);
                return blurred;
            });

            // Update UI on UI thread
            var tcs = new TaskCompletionSource();
            var queue = mainGrid.DispatcherQueue;
            if (queue == null)
            {
                processedBitmap.Dispose();
                return;
            }

            queue.TryEnqueue(() =>
            {
                try
                {
                    var wb = ConvertToWriteableBitmap(processedBitmap);
                    mainGrid.Background = new ImageBrush
                    {
                        ImageSource = wb,
                        Stretch = Stretch.UniformToFill,
                        Opacity = opacity
                    };
                }
                catch (Exception ex)
                {
                    AppLog.Log($"Background image failed: {ex.Message}");
                }
                finally
                {
                    processedBitmap.Dispose();
                    tcs.SetResult();
                }
            });

            await tcs.Task;
        }
        catch (Exception ex)
        {
            AppLog.Log($"Background image failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Async version of ApplySolidBackground that processes the image on a background thread.
    /// </summary>
    private static async Task ApplySolidBackgroundAsync(Grid mainGrid, AppSettings settings, bool applyBlur)
    {
        if (!string.IsNullOrEmpty(settings.BackgroundImagePath) && File.Exists(settings.BackgroundImagePath))
        {
            try
            {
                var path = settings.BackgroundImagePath;
                var radius = Math.Clamp(settings.BackgroundBlurRadius, 0, 255);
                var opacity = Math.Clamp(settings.BackgroundImageOpacity, 0, 1);

                // Heavy GDI+ work on background thread
                var processedBitmap = await Task.Run(() =>
                {
                    using var bitmap = new Bitmap(path);
                    if (applyBlur)
                    {
                        using var blurred = ApplyGaussianBlur(bitmap, radius);
                        return new Bitmap(blurred); // clone to release the using
                    }
                    return new Bitmap(bitmap);
                });

                // Update UI on UI thread
                var tcs = new TaskCompletionSource();
                var queue = mainGrid.DispatcherQueue;
                if (queue == null)
                {
                    processedBitmap.Dispose();
                    return;
                }

                queue.TryEnqueue(() =>
                {
                    try
                    {
                        var wb = ConvertToWriteableBitmap(processedBitmap);
                        mainGrid.Background = new ImageBrush
                        {
                            ImageSource = wb,
                            Stretch = Stretch.UniformToFill,
                            Opacity = opacity
                        };
                    }
                    catch (Exception ex)
                    {
                        AppLog.Log($"Background image failed: {ex.Message}");
                    }
                    finally
                    {
                        processedBitmap.Dispose();
                        tcs.SetResult();
                    }
                });

                await tcs.Task;
            }
            catch (Exception ex)
            {
                AppLog.Log($"Background image failed: {ex.Message}");
            }
        }
        else
        {
            var isDark = settings.Theme == AppSettings.ThemeMode.Dark ||
                         (settings.Theme == AppSettings.ThemeMode.FollowSystem && IsSystemDark());
            mainGrid.Background = new XamlSolidColorBrush(isDark
                ? Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E)
                : Windows.UI.Color.FromArgb(0xFF, 0xF0, 0xF0, 0xF0));
        }
    }

    /// <summary>
    /// Convert a GDI+ Bitmap to a WinUI WriteableBitmap.
    /// </summary>
    private static WriteableBitmap ConvertToWriteableBitmap(Bitmap bitmap)
    {
        var wb = new WriteableBitmap(bitmap.Width, bitmap.Height);
        using (var destStream = wb.PixelBuffer.AsStream())
        {
            var pixels = new byte[bitmap.Width * bitmap.Height * 4];
            BitmapData srcData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(srcData.Scan0, pixels, 0, pixels.Length);
            bitmap.UnlockBits(srcData);
            destStream.Write(pixels, 0, pixels.Length);
        }
        return wb;
    }

    private static bool TrySetCompositionBackdrop(Window window, AppSettings settings)
    {
        try
        {
            var backdropTarget = window.As<ICompositionSupportsSystemBackdrop>();
            if (backdropTarget == null)
            {
                AppLog.Log("Window does not support ICompositionSupportsSystemBackdrop");
                return false;
            }

            // Clean up previous controllers
            ClearBackdrop(window);

            // Create controller based on mode
            if (settings.BackgroundBlur == AppSettings.BlurMode.Mica && MicaController.IsSupported())
            {
                _micaController = new MicaController { Kind = MicaKind.Base };
                _backdropConfig = new SystemBackdropConfiguration
                {
                    IsInputActive = true,
                    Theme = SystemBackdropTheme.Default
                };

                _micaController!.AddSystemBackdropTarget(backdropTarget);
                _micaController!.SetSystemBackdropConfiguration(_backdropConfig);
                AppLog.Log("MicaController applied");
                return true;
            }
            else if (settings.BackgroundBlur == AppSettings.BlurMode.Acrylic && DesktopAcrylicController.IsSupported())
            {
                _acrylicController = new DesktopAcrylicController();
                _backdropConfig = new SystemBackdropConfiguration
                {
                    IsInputActive = true,
                    Theme = SystemBackdropTheme.Default
                };

                _acrylicController!.AddSystemBackdropTarget(backdropTarget);
                _acrylicController!.SetSystemBackdropConfiguration(_backdropConfig);
                AppLog.Log("DesktopAcrylicController applied");
                return true;
            }

            AppLog.Log($"Controller not supported: Mica={MicaController.IsSupported()}, Acrylic={DesktopAcrylicController.IsSupported()}");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Log($"TrySetCompositionBackdrop failed: {ex.Message}");
            return false;
        }
    }

    private static void ClearBackdrop(Window window)
    {
        if (_micaController != null)
        {
            try { _micaController.RemoveSystemBackdropTarget(window.As<ICompositionSupportsSystemBackdrop>()); } catch { }
            try { _micaController.Dispose(); } catch { }
            _micaController = null;
        }
        if (_acrylicController != null)
        {
            try { _acrylicController.RemoveSystemBackdropTarget(window.As<ICompositionSupportsSystemBackdrop>()); } catch { }
            try { _acrylicController.Dispose(); } catch { }
            _acrylicController = null;
        }
        _backdropConfig = null;
    }

    private static void ApplyBlurBackground(Grid mainGrid, AppSettings settings)
    {
        XamlBrush? bg = null;

        if (!string.IsNullOrEmpty(settings.BackgroundImagePath) && File.Exists(settings.BackgroundImagePath))
        {
            try
            {
                using var bitmap = new Bitmap(settings.BackgroundImagePath);
                int radius = Math.Clamp(settings.BackgroundBlurRadius, 0, 255);
                using var processed = ApplyGaussianBlur(bitmap, radius);
                ApplyOverlayTint(processed, settings.BackgroundBlur);

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

    private static void ApplyOverlayTint(Bitmap bitmap, AppSettings.BlurMode mode)
    {
        Color tintColor = mode switch
        {
            AppSettings.BlurMode.Mica => Color.FromArgb(30, 240, 240, 240),
            AppSettings.BlurMode.Acrylic => Color.FromArgb(60, 40, 40, 40),
            _ => Color.Transparent
        };

        if (tintColor == Color.Transparent) return;

        using var g = Graphics.FromImage(bitmap);
        using var brush = new SolidBrush(tintColor);
        g.FillRectangle(brush, 0, 0, bitmap.Width, bitmap.Height);
    }

    private static void ApplySolidBackground(Grid mainGrid, AppSettings settings, bool applyBlur)
    {
        XamlBrush? bg = null;

        if (!string.IsNullOrEmpty(settings.BackgroundImagePath) && File.Exists(settings.BackgroundImagePath))
        {
            try
            {
                using var bitmap = new Bitmap(settings.BackgroundImagePath);
                var processed = applyBlur ? ApplyGaussianBlur(bitmap, Math.Clamp(settings.BackgroundBlurRadius, 0, 255)) : new Bitmap(bitmap);
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
                if (applyBlur) processed.Dispose();
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

    public static void Cleanup()
    {
        // Cannot remove without window reference, just dispose
        if (_micaController != null) { try { _micaController.Dispose(); } catch { } _micaController = null; }
        if (_acrylicController != null) { try { _acrylicController.Dispose(); } catch { } _acrylicController = null; }
    }
}
