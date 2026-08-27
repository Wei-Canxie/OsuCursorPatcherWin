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
    private int _ovX;  // last window screen position (for UpdateLayeredWindow)
    private int _ovY;
    private bool _overlayVisible;
    private Bitmap? _renderCache;

    // Last region-rebuild keys.
    private double _lastRegionScale = -1.0;
    private double _lastAdditive = -1.0;
    private double _lastRegionAngle = double.NaN;
    private int _lastRegionWidth;
    private int _lastRegionHeight;

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
        bool visible)
    {
        _angle = angle;
        _scaleValue = scaleValue;
        _additiveOpacity = additiveOpacity;

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

        if (visible)
        {
            _ovX = x;
            _ovY = y;
            // Move the overlay.  CRITICAL: WinForms' SetBounds internally calls
            // SetWindowPos WITHOUT HWND_TOPMOST, which drops the overlay from the
            // topmost band every time we move it — Start menu / Action Center /
            // volume flyouts (themselves topmost) then cover it.  Immediately
            // re-stack it to HWND_TOPMOST after every move so it stays above
            // every DirectComposition XAML surface.
            SetBounds(x, y, width, height);
            NativeMethods.SetTopmost(Handle);
        }

        // Rebuild the rendered (semi-transparent) cursor when the content or
        // footprint changed; otherwise keep the cached bitmap.
        var contentChanged = Math.Abs(scaleValue - _lastRegionScale) >= 0.03
            || (double.IsNaN(_lastRegionAngle)
                || Math.Abs(NormalizeAngle(angle - _lastRegionAngle)) >= 4.0)
            || Math.Abs(_additiveOpacity - _lastAdditive) >= 0.02
            || width != _lastRegionWidth
            || height != _lastRegionHeight;

        if (visible && contentChanged)
        {
            UpdateRegion(width, height);
            _lastRegionScale = scaleValue;
            _lastRegionAngle = angle;
            _lastAdditive = _additiveOpacity;
            _lastRegionWidth = width;
            _lastRegionHeight = height;
        }

        // Layered window: every move needs a fresh UpdateLayeredWindow call so
        // the image follows the window position (SetBounds alone doesn't move
        // the layered surface).
        if (visible && _renderCache != null)
        {
            ApplyLayered(_renderCache, width, height);
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
            // Supersample 4x for smooth anti-aliased edges: draw the cursor at
            // 4x resolution, then bilinearly downsample to the target size.  The
            // extra passes cost little (small bitmap) and eliminate the jaggies
            // visible when a 30px-design cursor is scaled up on HiDPI screens.
            const int ss = 8;
            using (var hi = new Bitmap(width * ss, height * ss, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(hi))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    // Render at ss× by scaling the cursor footprint accordingly.
                    RenderCursor(g, width * ss, height * ss);
                }

                var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (var g2 = Graphics.FromImage(bmp))
                {
                    g2.Clear(Color.Transparent);
                    g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g2.SmoothingMode = SmoothingMode.HighQuality;
                    g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g2.DrawImage(hi, 0, 0, width, height);
                }

                _renderCache?.Dispose();
                _renderCache = bmp;
            }
        }
        catch (Exception ex)
        {
            // best effort; retry next frame — but log so failures aren't silent.
            try { Program.Log($"[Overlay] UpdateRegion failed: {ex}"); } catch { }
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
            try { Program.Log($"[Overlay] ApplyLayered failed: {ex}"); } catch { }
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
                    Marshal.Copy(data.Scan0 + y * data.Stride, new byte[stride], 0, 0); // noop to touch
                    // copy row
                    var row = new byte[stride];
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, stride);
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

        // Scale the design-size cursor image (312x442) down to the actual on-screen
        // cursor size.  The window is sized to BaseCursorWindowSize/BaseCursorWidth
        // = 160/30 = 5.333x the cursor width, so the rendered cursor occupies
        // 1/5.333 of the window footprint (before the elastic scale/rotation).
        const float cursorWindowRatio = (float)(BaseCursorWindowSize / BaseCursorWidth);
        var renderW = (float)width / cursorWindowRatio * (float)_scaleValue;
        var renderH = renderW * (_baseBitmap.Height / (float)_baseBitmap.Width);

        g.TranslateTransform(width / 2f, height / 2f);
        g.RotateTransform((float)_angle);

        var x = -renderW / 2f;
        var y = -renderH / 2f;

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
            // SetWindowPos with HWND_TOPMOST + SwpNoMove|SwpNoSize re-stacks the
            // overlay above every other topmost window (including Start menu,
            // Action Center, clipboard/volume flyouts — DirectComposition XAML
            // surfaces that are themselves topmost).
            bool ok = NativeMethods.SetTopmost(Handle);
            Program.Log($"[Overlay] BringToTopmost ok={ok}");
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
