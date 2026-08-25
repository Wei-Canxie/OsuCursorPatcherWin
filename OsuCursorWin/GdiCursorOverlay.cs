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

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020L;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_TOPMOST = 0x00000008L;
    private const int RgnOr = 2;
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
    private bool _overlayVisible;

    // Last region-rebuild keys.
    private double _lastRegionScale = -1.0;
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
            // Move the overlay.  CRITICAL: WinForms' SetBounds internally calls
            // SetWindowPos WITHOUT HWND_TOPMOST, which drops the overlay from the
            // topmost band every time we move it — Start menu / Action Center /
            // volume flyouts (themselves topmost) then cover it.  Immediately
            // re-stack it to HWND_TOPMOST after every move so it stays above
            // every DirectComposition XAML surface.
            SetBounds(x, y, width, height);
            NativeMethods.SetTopmost(Handle);
        }

        // Only rebuild the clipping region when the footprint actually changed.
        var changed = Math.Abs(scaleValue - _lastRegionScale) >= 0.03
            || (double.IsNaN(_lastRegionAngle)
                || Math.Abs(NormalizeAngle(angle - _lastRegionAngle)) >= 4.0)
            || width != _lastRegionWidth
            || height != _lastRegionHeight;

        if (changed)
        {
            UpdateRegion(width, height);
            _lastRegionScale = scaleValue;
            _lastRegionAngle = angle;
            _lastRegionWidth = width;
            _lastRegionHeight = height;
        }

        if (visible)
        {
            Invalidate();
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
            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                RenderCursor(g, width, height);
            }

            // Build an HRGN of opaque runs and apply it with the raw SetWindowRgn
            // P/Invoke.  CRITICAL: do NOT use the WinForms Form.Region property.
            // WinForms owns Form.Region and, on WM_WINDOWPOSCHANGED (which our
            // periodic SetWindowPos(HWND_TOPMOST) in BringToTopmost triggers),
            // re-applies the default (whole-window) region, wiping our clip and
            // exposing the black BackColor as a big block.  A raw SetWindowRgn
            // region is owned by the OS, survives SetWindowPos, and is never
            // touched by WinForms.
            var region = CreateRectRgn(0, 0, 0, 0); // empty
            var data = bmp.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                var stride = data.Stride;
                var scan0 = data.Scan0;
                for (var y = 0; y < height; y++)
                {
                    var row = (long)stride * y;
                    var x0 = -1;
                    for (var x = 0; x < width; x++)
                    {
                        // BGRA: alpha = 4th byte at offset 3
                        var alpha = Marshal.ReadByte(scan0 + (int)row + x * 4 + 3);
                        if (alpha >= AlphaThreshold && x0 < 0)
                        {
                            x0 = x;
                        }
                        else if (alpha < AlphaThreshold && x0 >= 0)
                        {
                            var rowRgn = CreateRectRgn(x0, y, x, y + 1);
                            CombineRgn(region, region, rowRgn, RgnOr);
                            DeleteObject(rowRgn);
                            x0 = -1;
                        }
                    }

                    if (x0 >= 0)
                    {
                        var rowRgn = CreateRectRgn(x0, y, width, y + 1);
                        CombineRgn(region, region, rowRgn, RgnOr);
                        DeleteObject(rowRgn);
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            // SetWindowRgn transfers ownership of the region to the window; the
            // OS frees it when the window is destroyed or replaced.
            SetWindowRgn(Handle, region, true);
            Program.Log($"[Overlay] region rebuilt {width}x{height} scale={_scaleValue:F2}");
        }
        catch (Exception ex)
        {
            // best effort; retry next frame — but log so failures aren't silent.
            try { Program.Log($"[Overlay] UpdateRegion failed: {ex}"); } catch { }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // No base.OnPaint and no Clear: the region already clips the window to
        // the cursor shape, so we just draw the cursor over whatever is there.
        if (!_overlayVisible)
        {
            return;
        }

        RenderCursor(e.Graphics, Width, Height);
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
