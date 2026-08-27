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
        // OCR_NORMAL: cursor.png (start menu / DC surface) — same bitmap the
        // overlay uses in normal scenes, so both stay visually unified.
        [NativeMethods.OCR_NORMAL] = null!, // handled specially in LoadOsuCursors
        [NativeMethods.OCR_IBEAM] = "text.cur",
        // req4: 转圈(等待软件开启、桌面右键等待) -> busy.png
        [NativeMethods.OCR_WAIT] = "busy.png",
        [NativeMethods.OCR_CROSS] = "cross.cur",
        [NativeMethods.OCR_UP] = null!,
        [NativeMethods.OCR_SIZENWSE] = "dgn1.cur",
        [NativeMethods.OCR_SIZENESW] = "dgn2.cur",
        [NativeMethods.OCR_SIZEWE] = "horz.cur",
        [NativeMethods.OCR_SIZENS] = "vert.cur",
        // req2: 拖动光标(看图、移动画布) -> hand.cur
        [NativeMethods.OCR_SIZEALL] = "hand.cur",
        [NativeMethods.OCR_NO] = "unavailiable.cur",
        // req3: 特殊点击(打开超链接、点击按钮) -> link.cur
        [NativeMethods.OCR_HAND] = "link.cur",
        // req5: 卡死(沙漏光标、软件卡死) -> work.ani
        [NativeMethods.OCR_APPSTARTING] = "work.ani",
        [NativeMethods.OCR_HELP] = "alternate.cur",
    };

    private static readonly Dictionary<uint, IntPtr> BlankHandles = new();
    private static readonly Dictionary<uint, IntPtr> OsuHandles = new();

    // For animated (.ani) cursors CopyIcon would lose the animation frames, so
    // we keep the temp-file path and reload the full animation via
    // LoadCursorFromFile on every mode switch instead.
    private static readonly Dictionary<uint, string> OsuAniTempPaths = new();
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

            // SetSystemCursor takes ownership of the handle it receives (it
            // destroys it).  Always pass a copy so our original stays valid for
            // later mode switches.
            var copy = NativeMethods.CopyIcon(blank);
            if (copy != IntPtr.Zero)
            {
                NativeMethods.SetSystemCursor(copy, id);
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

        int failed = 0;
        foreach (var id in CursorIds)
        {
            IntPtr original = IntPtr.Zero;
            if (useOsu && OsuHandles.TryGetValue(id, out var osu) && osu != IntPtr.Zero)
            {
                original = osu;
            }
            else if (BlankHandles.TryGetValue(id, out var blank))
            {
                original = blank;
            }

            if (original == IntPtr.Zero)
            {
                continue;
            }

            // SetSystemCursor DESTROYS the handle we pass it (the system takes
            // ownership).  For animated (.ani) cursors CopyIcon would lose the
            // animation frames, so we load a fresh copy via LoadCursorFromFile;
            // for static cursors we use CopyIcon to keep the original alive.
            IntPtr copy;
            if (useOsu && OsuAniTempPaths.TryGetValue(id, out var aniPath) && aniPath != null)
            {
                copy = NativeMethods.LoadCursorFromFile(aniPath);
            }
            else
            {
                copy = NativeMethods.CopyIcon(original);
            }

            if (copy == IntPtr.Zero)
            {
                failed++;
                continue;
            }

            if (!NativeMethods.SetSystemCursor(copy, id))
            {
                failed++;
                Program.Log($"SetMode({useOsu}) SetSystemCursor failed id={id} err={Marshal.GetLastWin32Error()}");
            }
            // copy was destroyed by SetSystemCursor (or is garbage — ignore it).
        }

        if (failed > CursorIds.Length / 2)
        {
            // Fallback: Windows may have invalidated our whole cursor set
            // (DPI/theme change).  Rebuild and retry once.
            Program.Log("Handles invalidated; reloading cursor set...");
            Reload();
            SetModeCore(useOsu);
            _osuMode = useOsu;
            Program.Log($"CursorReplacer.SetMode osu={useOsu} (after reload)");
            return;
        }

        _osuMode = useOsu;
        Program.Log($"CursorReplacer.SetMode osu={useOsu}");
    }

    private static void SetModeCore(bool useOsu)
    {
        foreach (var id in CursorIds)
        {
            IntPtr original = IntPtr.Zero;
            if (useOsu && OsuHandles.TryGetValue(id, out var osu) && osu != IntPtr.Zero)
            {
                original = osu;
            }
            else if (BlankHandles.TryGetValue(id, out var blank))
            {
                original = blank;
            }

            if (original == IntPtr.Zero)
            {
                continue;
            }

            var copy = NativeMethods.CopyIcon(original);
            if (copy == IntPtr.Zero)
            {
                continue;
            }

            NativeMethods.SetSystemCursor(copy, id);
        }
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
        OsuAniTempPaths.Clear();
        _installed = false;
        _osuMode = false;

        // Rebuild blanks + osu set from the cached source image.  Pass a CLONE
        // so Install's cache-refresh (`_cachedOsuImage?.Dispose()` then clone)
        // doesn't dispose the very object we're about to clone.
        using var clone = _cachedOsuImage != null ? new Bitmap(_cachedOsuImage) : null;
        Install(clone, _osuSizePx);
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
        OsuAniTempPaths.Clear();
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
        // OCR_NORMAL: cursor.png bitmap directly (req1 — unify DC surface with
        // normal scene).  The cursor sprite is the same image the overlay
        // draws, so both modes look identical at the pointer.
        if (osuImage != null)
        {
            var cursor = CreateCursorFromBitmap(osuImage, _osuSizePx);
            if (cursor != IntPtr.Zero)
            {
                OsuHandles[NativeMethods.OCR_NORMAL] = cursor;
            }
        }

        // OCR_IBEAM: programmatic white I-beam (vertical line + top/bottom bars)
        // instead of the osu-style circle from text.cur.
        {
            var ibeam = CreateIBeamCursor(_osuSizePx);
            if (ibeam != IntPtr.Zero)
            {
                OsuHandles[NativeMethods.OCR_IBEAM] = ibeam;
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

            // PNG source images are embedded under the Images.* prefix; .cur/.ani
            // under Cursors.*.
            string resName = filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? "OsuCursorWin.Images." + filename
                : CursorResPrefix + filename;
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

                IntPtr hcur;
                if (filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    // PNG source image -> build a cursor from its bitmap (the
                    // arrow+ring sprite for OCR_APPSTARTING).  We cannot load a
                    // PNG with LoadCursorFromFile.
                    using var ms = new MemoryStream(data);
                    using var bmp = new Bitmap(ms);
                    hcur = CreateCursorFromBitmap(bmp, _osuSizePx);
                    if (hcur == IntPtr.Zero)
                    {
                        Program.Log($"[CursorReplacer] CreateCursorFromBitmap failed for '{filename}'");
                        if (OsuHandles.TryGetValue(NativeMethods.OCR_NORMAL, out var fallback))
                        {
                            OsuHandles[id] = fallback;
                        }

                        continue;
                    }

                    OsuHandles[id] = hcur;
                    Program.Log($"[CursorReplacer] Loaded osu cursor for id={id} from '{filename}' (bitmap): hcur=0x{hcur.ToInt64():X}");
                    continue;
                }

                // Write to temp file, load via LoadCursorFromFile
                string tempPath = Path.Combine(tempDir, filename);
                File.WriteAllBytes(tempPath, data);
                hcur = NativeMethods.LoadCursorFromFile(tempPath);
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
                if (filename.EndsWith(".ani", StringComparison.OrdinalIgnoreCase))
                {
                    OsuAniTempPaths[id] = tempPath;
                }
                else if (filename.EndsWith(".cur", StringComparison.OrdinalIgnoreCase))
                {
                    // Hotspot policy:
                    //  - link.cur (OCR_HAND, hyperlink/button): top + 1/4 from
                    //    the left, per user req2.
                    //  - all other .cur: top-left (0,0) per user req6.
                    int hotX = 0, hotY = 0;
                    if (filename.Equals("link.cur", StringComparison.OrdinalIgnoreCase))
                    {
                        hotX = GetCursorBitmapWidth(hcur) / 4;
                        hotY = 0;
                    }

                    var relocated = SetHotspot(hcur, hotX, hotY);
                    if (relocated != IntPtr.Zero && relocated != hcur)
                    {
                        OsuHandles[id] = relocated;
                        NativeMethods.DestroyCursor(hcur);
                    }
                }

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
            return NativeMethods.CreateCursor(IntPtr.Zero, 0, 0, sizePx, sizePx, andMask, xorMask);
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

    /// <summary>Create a white I-beam HCURSOR (vertical bar with top/bottom
    /// caps) — the classic text cursor.  assets/ex/text.cur is an osu-style
    /// circle dot, which reads badly as a text caret, so we draw a real
    /// I-beam instead.  Hotspot is the centre of the vertical bar.</summary>
    private static IntPtr CreateIBeamCursor(int sizePx)
    {
        try
        {
            using var bmp = new Bitmap(sizePx, sizePx, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                float cx = sizePx / 2f;
                float barW = Math.Max(2f, sizePx * 0.08f);   // vertical bar width
                float capW = sizePx * 0.30f;                 // top/bottom cap width
                float capH = Math.Max(2f, sizePx * 0.10f);   // cap height
                float topY = sizePx * 0.10f;
                float botY = sizePx * 0.90f;

                using var brush = new SolidBrush(Color.White);
                // vertical bar
                g.FillRectangle(brush, cx - barW / 2f, topY, barW, botY - topY);
                // top cap
                g.FillRectangle(brush, cx - capW / 2f, topY, capW, capH);
                // bottom cap
                g.FillRectangle(brush, cx - capW / 2f, botY - capH, capW, capH);
            }

            // req3: text-selection cursor hotspot at CENTRE.
            return CreateColorCursorFromBitmap(bmp, sizePx, sizePx / 2, sizePx / 2);
        }
        catch (Exception ex)
        {
            Program.Log($"[CursorReplacer] CreateIBeamCursor failed: {ex}");
            return IntPtr.Zero;
        }
    }

    /// <summary>Return a copy of an HCURSOR with its hotspot relocated to
    /// (hotX, hotY).  The original handle is NOT destroyed — callers must
    /// dispose it separately.  Returns the original on failure.</summary>
    private static IntPtr SetHotspot(IntPtr hcur, int hotX, int hotY)
    {
        if (hcur == IntPtr.Zero) return hcur;
        try
        {
            if (!NativeMethods.GetIconInfo(hcur, out var info)) return hcur;
            info.fIcon = false;
            info.xHotspot = hotX;
            info.yHotspot = hotY;
            var result = NativeMethods.CreateIconIndirect(ref info);
            if (info.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(info.hbmMask);
            if (info.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(info.hbmColor);
            return result != IntPtr.Zero ? result : hcur;
        }
        catch (Exception ex)
        {
            Program.Log($"[CursorReplacer] SetHotspot failed: {ex}");
            return hcur;
        }
    }

    /// <summary>Get the width (in pixels) of an HCURSOR's colour/mask bitmap.</summary>
    private static int GetCursorBitmapWidth(IntPtr hcur)
    {
        try
        {
            if (!NativeMethods.GetIconInfo(hcur, out var info)) return 0;
            int width = 0;
            var hbm = info.hbmColor != IntPtr.Zero ? info.hbmColor : info.hbmMask;
            if (hbm != IntPtr.Zero && NativeMethods.GetObject(hbm, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.BITMAP>(), out var bm) != 0)
            {
                width = bm.bmWidth;
            }

            if (info.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(info.hbmMask);
            if (info.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(info.hbmColor);
            return width;
        }
        catch (Exception ex)
        {
            Program.Log($"[CursorReplacer] GetCursorBitmapWidth failed: {ex}");
            return 0;
        }
    }

    private static IntPtr CreateCursorFromBitmap(Bitmap source, int sizePx)
    {
        return CreateColorCursorFromBitmap(source, sizePx, 0, 0);
    }

    /// <summary>Create a colour HCURSOR from a source bitmap by writing a
    /// .cur file to the temp directory and loading it via LoadCursorFromFile.
    /// Unlike the old monochrome CreateCursor/AND+XOR approach, this preserves
    /// the image's real colours (e.g. cursor.png's grey arrow + white ring).
    /// Hotspot is set to (hotX, hotY).</summary>
    private static IntPtr CreateColorCursorFromBitmap(Bitmap source, int sizePx, int hotX, int hotY)
    {
        try
        {
            using var bmp = new Bitmap(sizePx, sizePx, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float scale = (float)sizePx / Math.Max(source.Width, source.Height);
                int w = (int)(source.Width * scale);
                int h = (int)(source.Height * scale);
                g.DrawImage(source, (sizePx - w) / 2, (sizePx - h) / 2, w, h);
            }

            int wOut = bmp.Width, hOut = bmp.Height;
            int stride = wOut * 4;
            byte[] pixelData = new byte[stride * hOut];
            var data = bmp.LockBits(new Rectangle(0, 0, wOut, hOut), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < hOut; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, pixelData, y * stride, stride);
                }
            }
            finally { bmp.UnlockBits(data); }

            // .cur DIB must be bottom-up: reverse the row order.
            byte[] bottomUp = new byte[pixelData.Length];
            for (int y = 0; y < hOut; y++)
            {
                Buffer.BlockCopy(pixelData, y * stride, bottomUp, (hOut - 1 - y) * stride, stride);
            }

            // 1-bit AND mask (rows 4-byte aligned).  bit set = transparent.
            int maskRowBytes = ((wOut + 31) / 32) * 4;
            byte[] andMask = new byte[maskRowBytes * hOut];
            for (int y = 0; y < hOut; y++)
            {
                for (int x = 0; x < wOut; x++)
                {
                    byte a = bottomUp[y * stride + x * 4 + 3]; // BGRA alpha
                    int byteIdx = y * maskRowBytes + (x / 8);
                    int bitMask = 0x80 >> (x % 8);
                    if (a < 60) andMask[byteIdx] |= (byte)bitMask;
                }
            }

            // Build ICONDIR + ICONDIRENTRY + DIB (XOR then AND).  biHeight is
            // POSITIVE (bottom-up) and DOUBLE (XOR height + AND height) — this
            // is what LoadCursorFromFile requires; negative/top-down fails.
            const int dibHeaderSize = 40;
            int dibSize = dibHeaderSize + bottomUp.Length + andMask.Length;
            int entryOffset = 6 + 16; // ICONDIR + ICONDIRENTRY = 22
            var cur = new byte[entryOffset + dibSize];

            // ICONDIR
            cur[2] = 2; // type = cursor
            cur[4] = 1; // count = 1

            // ICONDIRENTRY
            cur[6] = (byte)(wOut >= 256 ? 0 : wOut);
            cur[7] = (byte)(hOut >= 256 ? 0 : hOut);
            cur[10] = (byte)hotX;      // xHotspot low byte
            cur[11] = (byte)(hotX >> 8);
            cur[12] = (byte)hotY;      // yHotspot low byte
            cur[13] = (byte)(hotY >> 8);
            cur[14] = (byte)(dibSize & 0xFF);
            cur[15] = (byte)((dibSize >> 8) & 0xFF);
            cur[16] = (byte)((dibSize >> 16) & 0xFF);
            cur[17] = (byte)((dibSize >> 24) & 0xFF);
            cur[18] = (byte)(entryOffset & 0xFF);
            cur[19] = (byte)((entryOffset >> 8) & 0xFF);
            cur[20] = (byte)((entryOffset >> 16) & 0xFF);
            cur[21] = (byte)((entryOffset >> 24) & 0xFF);

            // BITMAPINFOHEADER (32-bit BGRA, bottom-up, height = 2*actual)
            int idx = entryOffset;
            WriteInt(cur, ref idx, dibHeaderSize); // biSize
            WriteInt(cur, ref idx, wOut);           // biWidth
            WriteInt(cur, ref idx, 2 * hOut);       // biHeight (positive, double)
            WriteShort(cur, ref idx, 1);             // biPlanes
            WriteShort(cur, ref idx, 32);            // biBitCount
            WriteInt(cur, ref idx, 0);               // biCompression (BI_RGB)
            WriteInt(cur, ref idx, 0);               // biSizeImage
            WriteInt(cur, ref idx, 0);               // biXPelsPerMeter
            WriteInt(cur, ref idx, 0);               // biYPelsPerMeter
            WriteInt(cur, ref idx, 0);               // biClrUsed
            WriteInt(cur, ref idx, 0);               // biClrImportant

            // XOR pixel data, then AND mask
            Buffer.BlockCopy(bottomUp, 0, cur, idx, bottomUp.Length);
            idx += bottomUp.Length;
            Buffer.BlockCopy(andMask, 0, cur, idx, andMask.Length);

            string tempDir = Path.Combine(Path.GetTempPath(), "OsuCursorWinCursors");
            Directory.CreateDirectory(tempDir);
            string tempPath = Path.Combine(tempDir, $"__color_{Guid.NewGuid():N}.cur");
            File.WriteAllBytes(tempPath, cur);

            var hcur = NativeMethods.LoadCursorFromFile(tempPath);
            try { File.Delete(tempPath); } catch { }
            return hcur;
        }
        catch (Exception ex)
        {
            Program.Log($"[CursorReplacer] CreateColorCursorFromBitmap failed: {ex}");
            return IntPtr.Zero;
        }
    }

    private static void WriteInt(byte[] buf, ref int pos, int val)
    {
        buf[pos++] = (byte)(val & 0xFF);
        buf[pos++] = (byte)((val >> 8) & 0xFF);
        buf[pos++] = (byte)((val >> 16) & 0xFF);
        buf[pos++] = (byte)((val >> 24) & 0xFF);
    }

    private static void WriteShort(byte[] buf, ref int pos, int val)
    {
        buf[pos++] = (byte)(val & 0xFF);
        buf[pos++] = (byte)((val >> 8) & 0xFF);
    }
}
