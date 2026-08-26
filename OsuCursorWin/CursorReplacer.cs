using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace OsuCursorWin;

/// <summary>
/// Replaces the native system cursors with blank (hidden) cursors in normal
/// scenes so the animated osu overlay is the only visible pointer.  When the
/// pointer moves over a DirectComposition XAML surface (Start menu, Action
/// Center, volume/clipboard flyouts) — where the overlay is invisible — swaps
/// to a complete osu!-themed cursor set from embedded resources:
///   OCR_NORMAL → white ring (from cursor.png)
///   OCR_IBEAM → text.cur, OCR_HAND → hand.cur, OCR_CROSS → cross.cur, etc.
/// Each system state gets a corresponding osu-style cursor, keeping the
/// pointer visible and themed on every surface.
/// </summary>
internal static class CursorReplacer
{
    private const uint SpiSetCursors = 0x0057;
    private const uint SpifSendChange = 0x0002;
    private const string CursorResPrefix = "OsuCursorWin.Cursors.";

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

    // Map OCR ID → embedded cursor resource filename (without prefix)
    private static readonly Dictionary<uint, string> OsuCursorMap = new()
    {
        [NativeMethods.OCR_NORMAL] = null!, // handled specially: white ring from cursor.png
        [NativeMethods.OCR_IBEAM] = "text.cur",
        [NativeMethods.OCR_WAIT] = "busy.ani",
        [NativeMethods.OCR_CROSS] = "cross.cur",
        [NativeMethods.OCR_UP] = null!, // no dedicated cursor, use white ring
        [NativeMethods.OCR_SIZENWSE] = "dgn1.cur",
        [NativeMethods.OCR_SIZENESW] = "dgn2.cur",
        [NativeMethods.OCR_SIZEWE] = "horz.cur",
        [NativeMethods.OCR_SIZENS] = "vert.cur",
        [NativeMethods.OCR_SIZEALL] = "move.cur",
        [NativeMethods.OCR_NO] = "unavailiable.cur",
        [NativeMethods.OCR_HAND] = "hand.cur",
        [NativeMethods.OCR_APPSTARTING] = "busy.ani",
        [NativeMethods.OCR_HELP] = "alternate.cur",
    };

    private static readonly Dictionary<uint, IntPtr> BlankHandles = new();
    private static readonly Dictionary<uint, IntPtr> OsuHandles = new();
    private static bool _installed;
    private static bool _osuMode;
    private static int _osuSizePx = 32;
    private static Bitmap? _cachedOsuImage;

    internal static bool Install(Bitmap? osuImage = null, int osuSizePx = 32)
    {
        if (_installed)
        {
            return true;
        }

        _osuSizePx = Math.Clamp(osuSizePx, 16, 96);

        // Keep a private copy so SetMode can rebuild the whole set if Windows
        // invalidates the cursor handles (DPI/theme changes destroy cursors set
        // via SetSystemCursor, leaving our stored handles dead).  Only refresh
        // when a real image is supplied (Reload passes null to reuse the cache).
        if (osuImage != null)
        {
            _cachedOsuImage?.Dispose();
            _cachedOsuImage = new Bitmap(osuImage);
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

        // Build osu-style cursors from embedded resources
        LoadOsuCursors(osuImage);

        _installed = BlankHandles.ContainsKey(NativeMethods.OCR_NORMAL);
        Program.Log($"CursorReplacer.Install installed={_installed} blank={BlankHandles.Count} osu={OsuHandles.Count}");
        if (OsuHandles.Count < CursorIds.Length)
        {
            Program.Log($"[CursorReplacer] Warning: only {OsuHandles.Count}/{CursorIds.Length} osu cursors loaded");
        }

        return _installed;
    }

    internal static void SetMode(bool useOsu)
    {
        if (!_installed)
        {
            return;
        }

        if (!ApplyMode(useOsu))
        {
            // Windows invalidates cursors installed via SetSystemCursor when the
            // DPI/theme changes (GetLastError=1402 ERROR_CURSOR_NOT_FOUND).
            // Rebuild the whole set from resources and retry once.
            Program.Log("Cursor handles invalidated; reloading cursor set...");
            Reload();
            ApplyMode(useOsu);
        }

        _osuMode = useOsu;
        Program.Log($"CursorReplacer.SetMode osu={useOsu}");
    }

    private static bool ApplyMode(bool useOsu)
    {
        var allOk = true;
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

            if (!NativeMethods.SetSystemCursor(handle, id))
            {
                allOk = false;
                Program.Log($"SetMode({useOsu}) SetSystemCursor failed id={id} err={Marshal.GetLastWin32Error()}");
            }
        }

        return allOk;
    }

    internal static bool IsOsuMode() => _osuMode;

    internal static IntPtr GetBlankHandle(uint cursorId)
    {
        return BlankHandles.TryGetValue(cursorId, out var handle) ? handle : IntPtr.Zero;
    }

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

        foreach (var value in OsuHandles.Values)
        {
            if (value == handle)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Rebuild the whole blank+osu cursor set from cached resources.
    /// Called when Windows invalidates our installed cursor handles (e.g. after
    /// a DPI or theme change), so SetSystemCursor stops failing with
    /// ERROR_CURSOR_NOT_FOUND.</summary>
    internal static void Reload()
    {
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

        // Install(false) path: recreate blanks + osu set, reusing _cachedOsuImage.
        Install(null, _osuSizePx);
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

    private static void LoadOsuCursors(Bitmap? osuImage)
    {
        // For OCR_NORMAL: white ring from cursor.png (or provided osuImage)
        if (osuImage != null)
        {
            var ring = CreateWhiteRingCursor(osuImage, _osuSizePx);
            if (ring != IntPtr.Zero)
            {
                OsuHandles[NativeMethods.OCR_NORMAL] = ring;
            }
        }

        // For all other IDs: load from embedded .cur/.ani resources
        var asm = Assembly.GetExecutingAssembly();
        string tempDir = Path.Combine(Path.GetTempPath(), "OsuCursorWinCursors");
        Directory.CreateDirectory(tempDir);

        foreach (var kv in OsuCursorMap)
        {
            uint id = kv.Key;
            string? filename = kv.Value;
            if (filename == null)
            {
                // No dedicated cursor; use white ring (already set for NORMAL, else fallback)
                if (!OsuHandles.ContainsKey(id) && OsuHandles.TryGetValue(NativeMethods.OCR_NORMAL, out var ring))
                {
                    OsuHandles[id] = ring;
                }

                continue;
            }

            if (OsuHandles.ContainsKey(id))
            {
                continue; // already loaded (e.g. OCR_NORMAL)
            }

            string resName = CursorResPrefix + filename;
            try
            {
                using var stream = asm.GetManifestResourceStream(resName);
                if (stream == null)
                {
                    Program.Log($"[CursorReplacer] Resource '{resName}' not found");
                    // fallback to white ring
                    if (OsuHandles.TryGetValue(NativeMethods.OCR_NORMAL, out var fallback))
                    {
                        OsuHandles[id] = fallback;
                    }

                    continue;
                }

                byte[] data = new byte[stream.Length];
                stream.ReadExactly(data);

                // Write to temp file, load via LoadCursorFromFile
                string tempPath = Path.Combine(tempDir, filename);
                File.WriteAllBytes(tempPath, data);
                var hcur = NativeMethods.LoadCursorFromFile(tempPath);
                if (hcur == IntPtr.Zero)
                {
                    Program.Log($"[CursorReplacer] LoadCursorFromFile failed for '{filename}' err={Marshal.GetLastWin32Error()}");
                    if (OsuHandles.TryGetValue(NativeMethods.OCR_NORMAL, out var fallback))
                    {
                        OsuHandles[id] = fallback;
                    }

                    continue;
                }

                OsuHandles[id] = hcur;
                Program.Log($"[CursorReplacer] Loaded osu cursor for id={id} from '{filename}': hcur=0x{hcur.ToInt64():X}");
            }
            catch (Exception ex)
            {
                Program.Log($"[CursorReplacer] Failed to load '{filename}': {ex.Message}");
                if (OsuHandles.TryGetValue(NativeMethods.OCR_NORMAL, out var fallback))
                {
                    OsuHandles[id] = fallback;
                }
            }
        }
    }

    /// <summary>Create a white ring HCURSOR from the osu cursor image (cursor.png)
    /// using AND/XOR masks.  The ring is drawn as white pixels on transparent
    /// background.
    ///
    /// IMPORTANT: assets/cursor.png is NOT a clean ring — it is the full osu
    /// cursor sprite (a large stylised arrow + trail with the ring in the lower
    /// right).  Scaling the whole sprite into a cursor and centring it on the
    /// pointer puts the visible ring offset from the pointer tip.  Instead we
    /// draw a clean, geometrically-centred white osu ring (annulus + small
    /// centre dot) with its hotspot at the exact centre, so the pointer tip is
    /// always the ring's centre.</summary>
    private static IntPtr CreateWhiteRingCursor(Bitmap source, int sizePx)
    {
        try
        {
            using var ring = new Bitmap(sizePx, sizePx, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(ring))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // osu-style cursor: a thin white annulus with a small filled
                // centre dot, both centred on the cursor tip (hotspot = centre).
                float dotRadius = sizePx * 0.12f;   // centre dot
                float outerR = sizePx * 0.45f;      // ring outer edge
                float innerR = sizePx * 0.34f;      // ring inner edge
                float c = sizePx / 2f;

                // Centre dot.
                using (var dotBrush = new SolidBrush(Color.White))
                {
                    g.FillEllipse(dotBrush, c - dotRadius, c - dotRadius,
                        dotRadius * 2f, dotRadius * 2f);
                }

                // Ring (annulus): fill outer circle white, then punch the inner
                // hole back to transparent with SourceCopy compositing.
                g.CompositingMode = CompositingMode.SourceCopy;
                using (var ringBrush = new SolidBrush(Color.White))
                {
                    g.FillEllipse(ringBrush, c - outerR, c - outerR, outerR * 2f, outerR * 2f);
                    g.CompositingMode = CompositingMode.SourceOver;
                    g.FillEllipse(new SolidBrush(Color.Transparent), c - innerR, c - innerR,
                        innerR * 2f, innerR * 2f);
                }
            }

            var (andMask, xorMask) = BuildMasks(ring);
            return NativeMethods.CreateCursor(IntPtr.Zero, sizePx / 2, sizePx / 2, sizePx, sizePx, andMask, xorMask);
        }
        catch (Exception ex)
        {
            Program.Log($"[CursorReplacer] CreateWhiteRingCursor failed: {ex}");
            return IntPtr.Zero;
        }
    }

    private static (byte[] and, byte[] xor) BuildMasks(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int strideBytes = (w + 31) / 32 * 4;
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
                        and[byteIdx] &= (byte)~bitMask;
                        xor[byteIdx] |= (byte)bitMask;
                    }
                    else
                    {
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