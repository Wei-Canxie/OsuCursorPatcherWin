using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OsuCursorWin;

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
    private static bool _installed;

    internal static bool Install()
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

            Program.Log($"Hidden system cursor id={id}");
            BlankHandles[id] = blank;
        }

        _installed = BlankHandles.ContainsKey(NativeMethods.OCR_NORMAL);
        Program.Log($"CursorReplacer.Install installed={_installed} count={BlankHandles.Count}");
        return _installed;
    }

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
            // Broadcast the change so every window picks up the restored cursors.
            NativeMethods.SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, SpifSendChange);
        }

        // Release the blank cursor handles we created, otherwise every
        // install/restore cycle (e.g. toggling the cursor from the tray)
        // leaks GDI handles.
        foreach (var handle in BlankHandles.Values)
        {
            NativeMethods.DestroyCursor(handle);
        }
        BlankHandles.Clear();
        _installed = false;
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
}
