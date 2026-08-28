using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OsuCursorWin;

internal static class NativeMethods
{
    internal const uint OCR_NORMAL = 32512;
    internal const uint OCR_IBEAM = 32513;
    internal const uint OCR_WAIT = 32514;
    internal const uint OCR_CROSS = 32515;
    internal const uint OCR_UP = 32516;
    internal const uint OCR_SIZENWSE = 32642;
    internal const uint OCR_SIZENESW = 32643;
    internal const uint OCR_SIZEWE = 32644;
    internal const uint OCR_SIZENS = 32645;
    internal const uint OCR_SIZEALL = 32646;
    internal const uint OCR_NO = 32648;
    internal const uint OCR_HAND = 32649;
    internal const uint OCR_APPSTARTING = 32650;
    internal const uint OCR_HELP = 32651;

    internal const int GWL_EXSTYLE = -20;
    internal const int GwlStyle = -16;
    internal const long WS_EX_TRANSPARENT = 0x00000020L;
    internal const int RgnOr = 2;
    internal const int RgnAnd = 1;
    internal const int RgnDiff = 4;
    internal const long WS_EX_TOOLWINDOW = 0x00000080L;
    internal const long WS_EX_LAYERED = 0x00080000L;
    internal const long WS_EX_TOPMOST = 0x00000008L;
    internal const long WS_EX_NOACTIVATE = 0x08000000L;
    internal const long WsThickFrame = 0x00040000L;
    internal const long WsMaximize = 0x01000000L;
    internal const int SmCxSizeFrame = 32;
    internal const int SmCySizeFrame = 33;
    internal const int SmXVirtualScreen = 76;
    internal const int SmCxVirtualScreen = 78;

    internal static readonly IntPtr HwndTopmost = new(-1);
    internal static readonly IntPtr HwndTop = IntPtr.Zero;

    internal const int GwHwndNext = 2;
    internal const int GwHwndPrev = 3;
    internal const int GaRoot = 2;

    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpAsyncWindowPos = 0x4000;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpShowWindow = 0x0040;
    internal const uint SwpHideWindow = 0x0080;
    internal const uint CursorShowing = 0x0001;
    internal const uint WmQuit = 0x0012;
    internal const int WmHotkey = 0x0312;
    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModNoRepeat = 0x4000;
    internal const byte VkH = 0x48;

    internal const int WhMouseLl = 14;
    internal const uint WmMouseMove = 0x0200;
    internal const uint WmLButtonDown = 0x0201;
    internal const uint WmLButtonUp = 0x0202;
    internal const uint WmRButtonDown = 0x0204;
    internal const uint WmRButtonUp = 0x0205;
    internal const uint WmMButtonDown = 0x0207;
    internal const uint WmMButtonUp = 0x0208;
    internal const uint WmXButtonDown = 0x020B;
    internal const uint WmXButtonUp = 0x020C;

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        internal uint biSize;
        internal int biWidth;
        internal int biHeight;
        internal ushort biPlanes;
        internal ushort biBitCount;
        internal uint biCompression;
        internal uint biSizeImage;
        internal int biXPelsPerMeter;
        internal int biYPelsPerMeter;
        internal uint biClrUsed;
        internal uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        internal BITMAPINFOHEADER bmiHeader;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ICONINFO
    {
        internal bool fIcon;
        internal int xHotspot;
        internal int yHotspot;
        internal IntPtr hbmMask;
        internal IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CURSORINFO
    {
        internal int cbSize;
        internal int flags;
        internal IntPtr hCursor;
        internal POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        internal POINT pt;
        internal uint mouseData;
        internal uint flags;
        internal uint time;
        internal IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        internal IntPtr hwnd;
        internal uint message;
        internal IntPtr wParam;
        internal IntPtr lParam;
        internal uint time;
        internal POINT pt;
    }

    internal delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    internal static bool GetCursorInfo(out CURSORINFO info)
    {
        info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        return GetCursorInfoNative(ref info);
    }

    private static readonly IntPtr[] StandardCursorHandles = BuildStandardCursorHandles();

    private static IntPtr[] BuildStandardCursorHandles()
    {
        uint[] ids =
        {
            OCR_NORMAL, OCR_IBEAM, OCR_WAIT, OCR_CROSS, OCR_UP,
            OCR_SIZENWSE, OCR_SIZENESW, OCR_SIZEWE, OCR_SIZENS,
            OCR_SIZEALL, OCR_NO, OCR_HAND, OCR_APPSTARTING, OCR_HELP
        };
        var result = new IntPtr[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            result[i] = LoadCursor(IntPtr.Zero, new IntPtr((long)ids[i]));
        }
        return result;
    }

    /// <summary>True when the handle is one of the standard Windows system
    /// cursors (arrow, I-beam, hand, resize, crosshair, ...). GetCursorInfo
    /// reports the handle the app requested, so a custom cursor (Snipaste
    /// tools, games) yields a handle that matches none of these.</summary>
    internal static bool IsStandardCursor(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        foreach (var h in StandardCursorHandles)
        {
            if (h == handle)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the handle is the standard arrow cursor
    /// (OCR_NORMAL).  Used to distinguish the "normal" pointer state from
    /// special states (resize, I-beam, hand, crosshair, ...) that should
    /// still show a themed system cursor even in normal scenes.</summary>
    internal static bool IsNormalArrowCursor(IntPtr handle)
    {
        return handle != IntPtr.Zero && handle == StandardCursorHandles[0];
    }

    internal static void SetOverlayVisible(IntPtr hwnd, bool visible)
    {
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | (visible ? SwpShowWindow : SwpHideWindow));
    }

    internal static bool SetTopmost(IntPtr hwnd)
    {
        return SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    internal static void SetClickThrough(IntPtr hwnd)
    {
        long exstyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        // NOTE: do NOT add WS_EX_LAYERED here. WPF layered windows (via
        // AllowsTransparency=true) fail to composite over Windows 11
        // DirectComposition surfaces (Start menu, Action Center, clipboard,
        // volume flyout). The overlay must be a normal window clipped with
        // SetWindowRgn instead (see MainWindow.UpdateCursorRegion).
        exstyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exstyle));
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    internal static long GetWindowStyle(IntPtr hwnd)
    {
        return GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
    }

    internal static void Move(IntPtr hwnd, int x, int y, int width, int height, bool visible = true)
    {
        // Async reposition: the synchronous SetWindowPos blocked the render
        // thread for ~4.2ms per call.  At 180Hz a frame budget is only 5.56ms
        // — the 4.2ms block ate 75% of it and caused visible "tail-drag".
        //
        // For a layered window, async is safe: UpdateLayeredWindow is only
        // called when content changes (not on every move), so the window
        // pixels are already correct — DWM composites the layered surface
        // at the new position on the next frame.  The ShowOverlay path uses
        // a separate synchronous SetWindowPos(SWP_SHOWWINDOW) to atomically
        // show the window at the correct position, so there is no ghost.
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height,
            SwpNoActivate | SwpNoZOrder | SwpAsyncWindowPos | (visible ? SwpShowWindow : SwpHideWindow));
    }

    internal static void MoveTopmost(IntPtr hwnd, int x, int y, int width, int height, bool visible = true)
    {
        SetWindowPos(hwnd, HwndTopmost, x, y, width, height, SwpNoActivate | (visible ? SwpShowWindow : SwpHideWindow));
    }

    internal static void MoveAbove(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, bool visible = true)
    {
        SetWindowPos(hwnd, insertAfter, x, y, width, height, SwpNoActivate | (visible ? SwpShowWindow : SwpHideWindow));
    }

    internal static void BringAbove(IntPtr hwnd, IntPtr insertAfter)
    {
        SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    internal static bool SetWindowRegion(IntPtr hwnd, IntPtr region)
    {
        // SetWindowRgn takes ownership of the region; the system frees it.
        return SetWindowRgn(hwnd, region, true);
    }

    internal static IntPtr CreateRectRgn(int left, int top, int right, int bottom)
    {
        return CreateRectRgnNative(left, top, right, bottom);
    }

    internal static int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode)
    {
        return CombineRgnNative(dest, src1, src2, mode);
    }

    internal static bool DeleteObject(IntPtr ho)
    {
        return DeleteObjectNative(ho);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll", EntryPoint = "CreateRectRgn")]
    private static extern IntPtr CreateRectRgnNative(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

    [DllImport("gdi32.dll", EntryPoint = "CombineRgn")]
    private static extern int CombineRgnNative(IntPtr hrgnDst, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
    private static extern bool DeleteObjectNative(IntPtr ho);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP bm);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAP
    {
        internal int bmType;
        internal int bmWidth;
        internal int bmHeight;
        internal int bmWidthBytes;
        internal ushort bmPlanes;
        internal ushort bmBitsPixel;
        internal IntPtr bmBits;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    /// <summary>Atomically show the window at a specific position.
    /// Combines SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW so the window
    /// appears at the target coords in a single frame — no intermediate flash
    /// at the stale position.</summary>
    internal static void ShowAndPosition(IntPtr hwnd, int x, int y, int width, int height)
    {
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height,
            SwpNoActivate | SwpNoZOrder | SwpShowWindow);
    }

    [DllImport("user32.dll", EntryPoint = "GetCursorInfo", SetLastError = true)]
    private static extern bool GetCursorInfoNative(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreateCursor(
        IntPtr hInst,
        int xHotSpot,
        int yHotSpot,
        int nWidth,
        int nHeight,
        byte[] pvANDPlane,
        byte[] pvXORPlane);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr LoadCursorFromFile(string lpFileName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CopyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyCursor(IntPtr hCursor);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, IntPtr lpBits);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int SetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, IntPtr lpBits, ref BITMAPINFO lpbmi, uint colorUse);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(IntPtr lpModuleName);

    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    internal static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();


    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("gdi32.dll")]
    internal static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateDC(string? lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(IntPtr hdc);

    internal const int VREFRESH = 116; // VREFRESH for GetDeviceCaps

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // DEVMODE layout matching the Win32 Unicode (TCHAR) definition.
    // dmFormName is TCHAR[32] = 64 bytes under Unicode.  Pack=1 eliminates
    // C# default struct alignment so the field offsets match the native
    // layout exactly.  An ANSI layout here would cause EnumDisplaySettingsW
    // (the default on modern Windows) to read dmDisplayFrequency from the
    // wrong offset, returning 0 or garbage for high-refresh panels.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;          // 0
        public ushort dmSpecVersion;          // 64
        public ushort dmDriverVersion;        // 66
        public ushort dmSize;                 // 68
        public ushort dmDriverExtra;          // 70
        public uint dmFields;                // 72
        public short dmOrientation;          // 76
        public short dmPaperSize;            // 78
        public short dmPaperLength;          // 80
        public short dmPaperWidth;           // 82
        public short dmScale;                // 84
        public short dmCopies;               // 86
        public short dmDefaultSource;        // 88
        public short dmPrintQuality;         // 90
        public int dmPositionX;              // 92 (POINTL)
        public int dmPositionY;              // 96
        public uint dmDisplayOrientation;    // 100
        public uint dmDisplayFixedOutput;    // 104
        public short dmColor;                // 108
        public short dmDuplex;               // 110
        public short dmYResolution;          // 112
        public short dmTTOption;             // 114
        public short dmCollate;              // 116
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;            // 118 → 64 bytes (TCHAR[32]) → 182
        public ushort dmLogPixels;            // 182
        public uint dmBitsPerPel;            // 184
        public uint dmPelsWidth;             // 188
        public uint dmPelsHeight;            // 192
        public uint dmDisplayFlags;          // 196
        public uint dmDisplayFrequency;      // 200  <-- refresh rate (Hz)
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    internal static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    internal static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    // High-resolution multimedia timer.  Raising the system timer resolution to
    // 1 ms lets DispatcherTimer (backed by SetTimer) actually fire at the
    // requested 240 Hz instead of being clamped by the default ~15.6 ms system
    // timer tick (~64 Hz), which manifested as the cursor being stuck at 60 fps
    // on high-refresh (144/180 Hz) displays.
    [DllImport("winmm.dll")]
    internal static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    internal static extern uint timeEndPeriod(uint uPeriod);

    // High-resolution multimedia timer callback (1 ms resolution), used to
    // drive the render loop far more precisely than DispatcherTimer (whose WPF
    // implementation lags to ~30 ms regardless of timeBeginPeriod).
    public delegate void TimeProc(uint uID, uint uMsg, IntPtr dwUser, IntPtr dw1, IntPtr dw2);

    [DllImport("winmm.dll")]
    internal static extern uint timeSetEvent(uint uDelay, uint uResolution, TimeProc lpTimeProc,
        IntPtr dwUser, uint fuEvent);

    [DllImport("winmm.dll")]
    internal static extern uint timeKillEvent(uint uTimerID);

    internal const uint TimePeriodic = 0x0001;
}
