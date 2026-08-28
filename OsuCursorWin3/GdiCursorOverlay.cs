using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OsuCursorWin;

/// <summary>
/// WinForms overlay that renders the osu-style cursor with pure GDI+.  A plain
/// Win32/WinForms window (non-layered, clipped with SetWindowRgn) is the ONLY
/// window type that composites above Windows 11 DirectComposition surfaces
/// (Start menu, Action Center, clipboard/volume flyouts).  WPF windows — even
/// with AllowsTransparency=false and SetWindowRgn — are drawn via a
/// DirectComposition path that those XAML surfaces render on top of, so they
/// become invisible there.  GDI+ runs on the classic GDI redirection path and
/// therefore wins over every surface.
/// </summary>
internal sealed class GdiCursorOverlay : Form
{
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern bool SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr d, IntPtr a, IntPtr b, int m);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref NativeMethods.POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref NativeMethods.POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020L;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_TOPMOST = 0x00000008L;
    private const long WS_EX_LAYERED = 0x00080000L;
    private const int RgnOr = 2;
    private const uint ULW_ALPHA = 0x00000002;
    private const int AlphaThreshold = 60;
    private const int WmNcHitTest = 0x0084;
    private const int HitTestTransparent = -1;

    // Mirror the cursor geometry constants from MainWindow so the overlay knows
    // how the window footprint relates to the rendered cursor size.
    private const double BaseCursorWidth = 30.0;
    private const double BaseCursorWindowSize = 160.0;

    private readonly Bitmap _baseBitmap;
    private readonly Bitmap _additiveBitmap;

    // Current visual state (driven from MainWindow each frame).
    private double _angle;
    private double _scaleValue = 1.0;
    private double _additiveOpacity;

    // Req 2a: per-scene geometry tuning (set from MainWindow via UpdateState).
    private double _aspectX = 1.0;
    private double _aspectY = 1.0;
    private double _hotspotX; // physical px offset from the tuned anchor
    private double _hotspotY;
    private int _ovX;  // last window screen position (for UpdateLayeredWindow)
    private int _ovY;
    private bool _overlayVisible;
    private Bitmap? _renderCache;

    /// <summary>Mark content dirty so the next frame forces a rebuild.</summary>
    private double _cursorWidth = BaseCursorWidth;

    public void SetCursorWidth(double w)
    {
        if (Math.Abs(w - _cursorWidth) >= 0.5)
        {
            _cursorWidth = w;
            Invalidate();
        }
    }

    public void Invalidate()
    {
        _lastRegionScale = -1.0;
        _lastAdditive = -1.0;
        _lastRegionAngle = double.NaN;
        _lastAspectX = -1.0;
        _lastAspectY = -1.0;
        _lastHotspotX = double.NaN;
        _lastHotspotY = double.NaN;
        _lastRegionWidth = -1;
        _lastRegionHeight = -1;
    }

    // Last region-rebuild keys.
    private double _lastRegionScale = -1.0;
    private double _lastAdditive = -1.0;
    private double _lastRegionAngle = double.NaN;
    private double _lastAspectX = 1.0;
    private double _lastAspectY = 1.0;
    private double _lastHotspotX;
    private double _lastHotspotY;
    private int _lastRegionWidth;
    private int _lastRegionHeight;

    // PERF DIAG stats
    internal long _statSetBoundsTicks, _statRenderTicks, _statUlwTicks;
    internal int _statSetBoundsCount, _statRenderCount, _statUlwCount;

    public GdiCursorOverlay()
    {
        _baseBitmap = LoadPng("OsuCursorWin.Images.cursor.png");
        _additiveBitmap = LoadPng("OsuCursorWin.Images.cursorAdditive.png");

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black; // clipped away by SetWindowRgn
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    private static Bitmap LoadPng(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource: {resourceName}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;
        return new Bitmap(ms);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // ONLY WS_EX_TOPMOST.  Deliberately NO WS_EX_LAYERED (fails to
            // composite over Win11 DirectComposition surfaces), NO WS_EX_TRANSPARENT
            // (interferes with raw SetWindowRgn on non-layered windows), and NO
            // WS_EX_NOACTIVATE / WS_EX_TOOLWINDOW — experiments show these drop
            // the window below DirectComposition XAML surfaces (Start menu etc.)
            // even when set HWND_TOPMOST.  Click-through is handled by the WndProc
            // WM_NCHITTEST→HTTRANSPARENT override; taskbar hiding by ShowInTaskbar=false.
            cp.ExStyle |= (int)WS_EX_TOPMOST;
            cp.ExStyle |= (int)WS_EX_TRANSPARENT;
            cp.ExStyle |= (int)WS_EX_LAYERED;
            return cp;
        }
    }

    /// <summary>Show and activate the overlay window on the current desktop.</summary>
    public void ShowOverlay()
    {
        if (!IsHandleCreated)
        {
            CreateHandle();
        }

        if (!_overlayVisible)
        {
            _overlayVisible = true;
            Show();
        }
    }

    /// <summary>Hide the overlay window.</summary>
    public void HideOverlay()
    {
        if (_overlayVisible)
        {
            _overlayVisible = false;
            Hide();
        }
    }

    /// <summary>Update animation state and move/resize/reposition the overlay.</summary>
    public void UpdateState(
        int x, int y, int width, int height,
        double angle, double scaleValue, double additiveOpacity,
        bool visible,
        double aspectX = 1.0, double aspectY = 1.0,
        double hotspotX = 0.0, double hotspotY = 0.0)
    {
        _angle = angle;
        _scaleValue = scaleValue;
        _additiveOpacity = additiveOpacity;
        _aspectX = aspectX;
        _aspectY = aspectY;
        _hotspotX = hotspotX;
        _hotspotY = hotspotY;

        if (visible != _overlayVisible)
        {
            if (visible)
            {
                ShowOverlay();
            }
            else
            {
                HideOverlay();
                return;
            }
        }

        // Move-guard: only reposition when the cursor moved beyond a small
        // hysteresis deadband.  The low-level mouse hook reports sub-pixel
        // jitter even when the pointer is stationary; moving on every 1px tick
        // made SetWindowPos fire constantly (~45x/s while idle) and each
        // HWND_TOPMOST call forces a DWM re-composition — a big frame-time
        // cost.  A 2px deadband keeps idle frames at zero SetWindowPos calls.
        const int deadband = 2;
        var moved = visible
            && (Math.Abs(x - _ovX) >= deadband
                || Math.Abs(y - _ovY) >= deadband
                || width != _lastRegionWidth
                || height != _lastRegionHeight);

        if (visible && moved)
        {
            _ovX = x;
            _ovY = y;

            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            // PERF: pure reposition WITHOUT HWND_TOPMOST.  Re-stacking on every
            // move forced a DWM Z-order recomposition each frame (~5ms), making
            // per-frame cost exceed the 8ms target.  The MainWindow topmost
            // timer already calls BringToTopmost every 250ms, so z-order is
            // maintained via that periodic re-stack instead — the hot path
            // (every 8ms) is now a cheap SetWindowPos-with-SWP_NOZORDER.
            NativeMethods.Move(Handle, x, y, width, height);
            _statSetBoundsTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            _statSetBoundsCount++;
        }

        // Rebuild the rendered (semi-transparent) cursor when the content or
        // footprint changed; otherwise keep the cached bitmap.
        var contentChanged = Math.Abs(scaleValue - _lastRegionScale) >= 0.03
            || (double.IsNaN(_lastRegionAngle)
                || Math.Abs(NormalizeAngle(angle - _lastRegionAngle)) >= 4.0)
            || Math.Abs(_additiveOpacity - _lastAdditive) >= 0.02
            || Math.Abs(_aspectX - _lastAspectX) >= 0.01
            || Math.Abs(_aspectY - _lastAspectY) >= 0.01
            || Math.Abs(_hotspotX - _lastHotspotX) >= 0.5
            || Math.Abs(_hotspotY - _lastHotspotY) >= 0.5
            || width != _lastRegionWidth
            || height != _lastRegionHeight;

        if (visible && contentChanged)
        {
            var r0 = System.Diagnostics.Stopwatch.GetTimestamp();
            UpdateRegion(width, height);
            _statRenderTicks += System.Diagnostics.Stopwatch.GetTimestamp() - r0;
            _statRenderCount++;
            _lastRegionScale = scaleValue;
            _lastRegionAngle = angle;
            _lastAdditive = _additiveOpacity;
            _lastAspectX = _aspectX;
            _lastAspectY = _aspectY;
            _lastHotspotX = _hotspotX;
            _lastHotspotY = _hotspotY;
            _lastRegionWidth = width;
            _lastRegionHeight = height;
        }

        // Upload the rendered cursor image only when the content changed.
        // SetWindowPos (via SetBounds) already moves the window — the layered
        // window's pixel contents follow the window position automatically.
        // Calling UpdateLayeredWindow on every mouse move was a 3x cost
        // multiplier (SetBounds + SetTopmost + ULW) that capped the frame rate.
        // ULW is only needed when the bitmap itself changes.
        if (visible && contentChanged && _renderCache != null)
        {
            var a0 = System.Diagnostics.Stopwatch.GetTimestamp();
            ApplyLayered(_renderCache, width, height);
            _statUlwTicks += System.Diagnostics.Stopwatch.GetTimestamp() - a0;
            _statUlwCount++;
        }
    }

    private void UpdateRegion(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        try
        {
            // Render the cursor (base + additive, with per-pixel alpha) into a
            // cached 32-bit ARGB bitmap.  UpdateState calls ApplyLayered on every
            // move to re-upload it at the new position.
            //
            // PERF: supersampling was removed (was 8x + bicubic downsample).
            // Drawing directly at target size cuts the per-frame render cost
            // roughly in half, which matters for getting frame time under 8ms.
            // Edge smoothness relies on GDI+ HighQualityBicubic interpolation.
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                RenderCursor(g, width, height);
            }

            _renderCache?.Dispose();
            _renderCache = bmp;
        }
        catch (Exception ex)
        {
            // best effort; retry next frame — but log so failures aren't silent.
            try { AppLog.Log($"[Overlay] UpdateRegion failed: {ex}"); } catch { }
        }
    }

    private void ApplyLayered(Bitmap bmp, int width, int height)
    {
        if (!IsHandleCreated) return;
        try
        {
            UpdateLayered(bmp, width, height);
        }
        catch (Exception ex)
        {
            try { AppLog.Log($"[Overlay] ApplyLayered failed: {ex}"); } catch { }
        }
    }

    private void UpdateLayered(Bitmap bmp, int width, int height)
    {
        if (!IsHandleCreated) return;

        // Build a top-down 32-bit BGRA DIB (per-pixel alpha).
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = width;
        bmi.bmiHeader.biHeight = -height; // top-down
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr bits;
        IntPtr hbmp = CreateDIBSection(screenDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
        if (hbmp == IntPtr.Zero) { ReleaseDC(IntPtr.Zero, screenDc); return; }

        try
        {
            // Copy bitmap pixels (BGRA) into the DIB.
            var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = width * 4;
                for (int y = 0; y < height; y++)
                {
                    // copy row from the GDI+ bitmap (straight alpha, BGRA order)
                    var row = new byte[stride];
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, stride);

                    // Premultiply: UpdateLayeredWindow(AlphaFormat=AC_SRC_ALPHA=1)
                    // requires premultiplied alpha.  GDI+ Format32bppArgb / PNG
                    // sources are straight alpha; sending straight to ULW makes
                    // semi-transparent edges (RGB > A*255) render as a bright
                    // halo / jaggies — the exact "锯齿感" the user reported.
                    // Premultiply: RGB = RGB * A / 255, BGRA byte order.
                    for (int i = 0; i < stride; i += 4)
                    {
                        byte a = row[i + 3];
                        if (a == 0)
                        {
                            row[i] = 0; row[i + 1] = 0; row[i + 2] = 0;
                        }
                        else if (a < 255)
                        {
                            row[i]     = (byte)(row[i]     * a / 255); // B
                            row[i + 1] = (byte)(row[i + 1] * a / 255); // G
                            row[i + 2] = (byte)(row[i + 2] * a / 255); // R
                        }
                        // a == 255: RGB unchanged
                    }

                    Marshal.Copy(row, 0, bits + y * stride, stride);
                }
            }
            finally { bmp.UnlockBits(data); }

            IntPtr memDc = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) return;
            IntPtr old = SelectObject(memDc, hbmp);

            var dst = new NativeMethods.POINT { X = _ovX, Y = _ovY };
            var sz = new SIZE { cx = width, cy = height };
            var src = new NativeMethods.POINT { X = 0, Y = 0 };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref sz, memDc, ref src, 0, ref blend, ULW_ALPHA);

            SelectObject(memDc, old);
            DeleteDC(memDc);
        }
        finally
        {
            DeleteObject(hbmp);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Layered window: all pixels come from UpdateLayeredWindow.  Nothing to
        // draw here (drawing to the client DC would fight the layered surface
        // and cause flicker).  base.OnPaint is intentionally not called.
    }

    /// <summary>Draw the base + additive cursor images centered, scaled and rotated.</summary>
    private void RenderCursor(Graphics g, int width, int height)
    {
        // CRITICAL: transparent background, NOT Color.Black.  UpdateRegion scans
        // this rendering's alpha to build the clip region; an opaque black fill
        // makes every pixel alpha=255 so the region degenerates to the whole
        // window and the black BackColor shows as a big block.
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // Match the DC scene: scale the image so its height fills the canvas
        // (cursorWidth), and the width is proportional to the image aspect ratio.
        var renderH = (float)_cursorWidth * (float)_scaleValue * Math.Max(0.05f, (float)_aspectY);
        var renderW = renderH * (_baseBitmap.Width / (float)_baseBitmap.Height) * Math.Max(0.05f, (float)_aspectX);

        // Match the DC scene: the cursor image is drawn CENTERED in the canvas,
        // and the hotspot is at (canvasSize/8, canvasSize/8).  Rotation pivots
        // on the hotspot so the cursor tip stays locked to the pointer.
        var anchorX = width * 0.125f;
        var anchorY = height * 0.125f;
        g.TranslateTransform(anchorX, anchorY);
        g.RotateTransform((float)_angle);

        // After translating to the hotspot, draw the image so it appears centered
        // in the window.  The image center is at (width/2, height/2) in window
        // coords, which is (width/2 - width/8, height/2 - height/8) = (3*width/8, 3*height/8)
        // relative to the hotspot.
        var x = width * 0.375f - renderW * 0.5f + (float)_hotspotX;
        var y = height * 0.375f - renderH * 0.5f + (float)_hotspotY;

        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawImage(_baseBitmap, x, y, renderW, renderH);

        if (_additiveOpacity > 0.001f)
        {
            using var ia = new ImageAttributes();
            var cm = new ColorMatrix();
            cm.Matrix33 = Math.Clamp((float)_additiveOpacity, 0f, 1f);
            ia.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
            g.DrawImage(_additiveBitmap, new Rectangle((int)x, (int)y, (int)renderW, (int)renderH),
                0, 0, _baseBitmap.Width, _baseBitmap.Height, GraphicsUnit.Pixel, ia);
        }

        g.ResetTransform();
    }

    protected override void WndProc(ref Message m)
    {
        // Make the whole window click-through.  WM_NCHITTEST returning
        // HTTRANSPARENT passes the hit to the window below — the cursor shape
        // never intercepts clicks, and we don't need WS_EX_TRANSPARENT (which
        // breaks the raw SetWindowRgn rendering on non-layered windows).
        if (m.Msg == WmNcHitTest)
        {
            m.Result = (IntPtr)HitTestTransparent;
            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>
    /// Force the overlay window to the top of the topmost Z-order.
    /// WinForms TopMost=true property only sets HWND_TOPMOST once at creation;
    /// when another topmost window (Start menu, Action Center, etc.) opens
    /// later, it appears above us.  Call this periodically (e.g. every 250 ms)
    /// to stay on top of every surface including DirectComposition XAML surfaces.
    /// </summary>
    public void BringToTopmost()
    {
        if (IsHandleCreated && Visible)
        {
            bool ok = NativeMethods.SetTopmost(Handle);
        }
    }

    protected override void Dispose(bool disposing)
    {
        _renderCache?.Dispose();
        _renderCache = null;
        base.Dispose(disposing);
    }

    private static double NormalizeAngle(double degrees)
    {
        degrees %= 360.0;
        if (degrees > 180.0)
        {
            degrees -= 360.0;
        }
        else if (degrees < -180.0)
        {
            degrees += 360.0;
        }

        return degrees;
    }
}
