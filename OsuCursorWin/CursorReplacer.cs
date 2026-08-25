using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace OsuCursorWin;

/// <summary>
/// Replaces the native system cursors with either blank (hidden) cursors or an
/// osu-style ring cursor.  A hardware/system cursor is ALWAYS drawn on top of
/// every window — including Windows 11 DirectComposition XAML surfaces (Start
/// menu, Action Center, clipboard/volume flyouts) that a normal topmost window
/// cannot cover.  So: in ordinary scenes we hide the system cursor (blank) and
/// draw the animated osu overlay; when the cursor moves over one of those
/// DirectComposition surfaces (where the overlay is invisible underneath), we
/// switch the system cursor to a static osu ring so the pointer stays visible.
/// </summary>
internal static class CursorReplacer
{
    private const uint SpiSetCursors = 0x0057;
    private const uint SpifSendChange = 0x0002;

    private static readonly uint[] CursorIds =
    {
        NativeMethods.OCR_NORMAL,
        NativeMethods.OCR_IBEAM,
        NativeMethods.OCR_WAIT,
        NativeMethods.OCR_CROSS,
        NativeMethods.OCR_UP,
        NativeMethods.OCR_SIZENWSE,
        NativeMethods.OCR_SIZENESW,
        NativeMethods.OCR_SIZEWE,
        NativeMethods.OCR_SIZENS,
        NativeMethods.OCR_SIZEALL,
        NativeMethods.OCR_NO,
        NativeMethods.OCR_HAND,
        NativeMethods.OCR_APPSTARTING,
        NativeMethods.OCR_HELP
    };

    private static readonly Dictionary<uint, IntPtr> BlankHandles = new();
    private static readonly Dictionary<uint, IntPtr> OsuHandles = new();
    private static bool _installed;
    private static bool _osuMode;

    /// <summary>Install blank cursors so the animated osu overlay is the only
    /// visible pointer.  Also pre-build the osu system cursor handles (used to
    /// keep the pointer visible over DirectComposition surfaces).</summary>
    internal static bool Install(Bitmap? osuImage = null, int osuSizePx = 32)
    {
        if (_installed)
        {
            return true;
        }

        foreach (var id in CursorIds)
        {
            var blank = CreateBlankCursor();
            if (blank == IntPtr.Zero)
            {
                Program.Log($"CreateCursor failed for id={id} error={Marshal.GetLastWin32Error()}");
                continue;
            }

            if (!NativeMethods.SetSystemCursor(blank, id))
            {
                Program.Log($"SetSystemCursor failed for id={id} error={Marshal.GetLastWin32Error()}");
                NativeMethods.DestroyCursor(blank);
                continue;
            }

            BlankHandles[id] = blank;
        }

        // Pre-build osu-style system cursors (one per cursor id) so we can swap
        // to them instantly when the pointer enters a DirectComposition surface.
        if (osuImage != null)
        {
            var osu = CreateOsuCursors(osuImage, osuSizePx);
            if (osu != null)
            {
                OsuHandles.Clear();
                foreach (var kv in osu)
                {
                    OsuHandles[kv.Key] = kv.Value;
                }
            }
        }

        _installed = BlankHandles.ContainsKey(NativeMethods.OCR_NORMAL);
        Program.Log($"CursorReplacer.Install installed={_installed} blank={BlankHandles.Count} osu={OsuHandles.Count}");
        return _installed;
    }

    /// <summary>Switch the live system cursors to blank (overlay animation mode)
    /// or osu ring (DirectComposition-surface mode).</summary>
    internal static void SetMode(bool useOsu)
    {
        if (!_installed || useOsu == _osuMode)
        {
            return;
        }

        _osuMode = useOsu;
        foreach (var id in CursorIds)
        {
            IntPtr handle = IntPtr.Zero;
            if (useOsu && OsuHandles.TryGetValue(id, out var osu) && osu != IntPtr.Zero)
            {
                handle = osu;
            }
            else if (BlankHandles.TryGetValue(id, out var blank))
            {
                handle = blank;
            }

            if (handle == IntPtr.Zero)
            {
                continue;
            }

            // Re-apply the chosen cursor to the system.  SetSystemCursor copies
            // the cursor, so our stored handles remain owned by us and valid.
            if (!NativeMethods.SetSystemCursor(handle, id))
            {
                Program.Log($"SetMode({useOsu}) SetSystemCursor failed id={id} err={Marshal.GetLastWin32Error()}");
            }
        }

        Program.Log($"CursorReplacer.SetMode osu={useOsu}");
    }

    internal static bool IsOsuMode() => _osuMode;

    internal static IntPtr GetBlankHandle(uint cursorId)
    {
        return BlankHandles.TryGetValue(cursorId, out var handle) ? handle : IntPtr.Zero;
    }

    /// <summary>True when the given cursor handle is one of the blank cursors we
    /// installed to hide the native pointer. When an app shows its own custom
    /// cursor (Snipaste, games, etc.), the handle is foreign and we should step
    /// aside so the osu overlay doesn't double-draw over it.</summary>
    internal static bool IsInstalledCursor(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        foreach (var value in BlankHandles.Values)
        {
            if (value == handle)
            {
                return true;
            }
        }

        return false;
    }

    internal static void Restore()
    {
        if (!_installed)
        {
            return;
        }

        var restored = NativeMethods.SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, SpifSendChange);
        Program.Log(restored
            ? "Restore system cursors ok=True"
            : $"Restore system cursors ok=False error={Marshal.GetLastWin32Error()}");
        if (!restored)
        {
            RestoreDefaultCursors();
            NativeMethods.SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, SpifSendChange);
        }

        foreach (var handle in BlankHandles.Values)
        {
            NativeMethods.DestroyCursor(handle);
        }

        BlankHandles.Clear();

        foreach (var handle in OsuHandles.Values)
        {
            NativeMethods.DestroyCursor(handle);
        }

        OsuHandles.Clear();
        _installed = false;
        _osuMode = false;
    }

    private static void RestoreDefaultCursors()
    {
        foreach (var id in CursorIds)
        {
            var original = NativeMethods.LoadCursor(IntPtr.Zero, new IntPtr((long)id));
            if (original == IntPtr.Zero)
            {
                continue;
            }

            var copy = NativeMethods.CopyIcon(original);
            if (copy != IntPtr.Zero)
            {
                NativeMethods.SetSystemCursor(copy, id);
            }
        }

        Program.Log("Restored cursors from default system cursor handles.");
    }

    private static IntPtr CreateBlankCursor()
    {
        var andMask = new byte[128];
        Array.Fill(andMask, (byte)0xFF);
        var xorMask = new byte[128];
        return NativeMethods.CreateCursor(IntPtr.Zero, 0, 0, 32, 32, andMask, xorMask);
    }

    /// <summary>Build one osu-style system cursor per OCR id from the given image.
    /// The ring is drawn white; the background is made transparent via the AND
    /// plane so only the ring shows wherever the hardware cursor is drawn.</summary>
    private static Dictionary<uint, IntPtr>? CreateOsuCursors(Bitmap source, int sizePx)
    {
        var result = new Dictionary<uint, IntPtr>();
        try
        {
            using var ring = new Bitmap(sizePx, sizePx, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(ring))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                // The source is the osu ring (312x442).  Fit it into sizePx while
                // preserving aspect, centered.
                float scale = (float)sizePx / Math.Max(source.Width, source.Height);
                int w = (int)(source.Width * scale);
                int h = (int)(source.Height * scale);
                g.DrawImage(source, (sizePx - w) / 2, (sizePx - h) / 2, w, h);
            }

            var (andMask, xorMask) = BuildMasks(ring);

            foreach (var id in CursorIds)
            {
                var cur = NativeMethods.CreateCursor(IntPtr.Zero, sizePx / 2, sizePx / 2, sizePx, sizePx, andMask, xorMask);
                if (cur != IntPtr.Zero)
                {
                    result[id] = cur;
                }
            }
        }
        catch (Exception ex)
        {
            Program.Log($"[CursorReplacer] CreateOsuCursors failed: {ex}");
            foreach (var h in result.Values)
            {
                NativeMethods.DestroyCursor(h);
            }

            return null;
        }

        return result;
    }

    /// <summary>Convert a 32bpp ring bitmap into CreateCursor AND/XOR monochrome
    /// mask planes.  Ring pixels (alpha above threshold) are drawn (AND=0,
    /// XOR=1 -> white); transparent pixels keep the background (AND=1).</summary>
    private static (byte[] and, byte[] xor) BuildMasks(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int strideBytes = (w + 31) / 32 * 4; // 1bpp, row aligned to 32 bits
        int totalBytes = strideBytes * h;
        var and = new byte[totalBytes];
        var xor = new byte[totalBytes];

        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int pixel = Marshal.ReadInt32(data.Scan0, y * data.Stride + x * 4);
                    byte a = (byte)((pixel >> 24) & 0xFF);
                    bool draw = a >= 60;
                    int byteIdx = y * strideBytes + (x / 8);
                    int bitMask = 0x80 >> (x % 8);
                    if (draw)
                    {
                        // AND=0 (draw), XOR=1 (white)
                        and[byteIdx] &= (byte)~bitMask;
                        xor[byteIdx] |= (byte)bitMask;
                    }
                    else
                    {
                        // AND=1 (transparent), XOR=0
                        and[byteIdx] |= (byte)bitMask;
                        xor[byteIdx] &= (byte)~bitMask;
                    }
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return (and, xor);
    }
}
