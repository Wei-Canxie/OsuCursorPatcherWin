using System;
using System.Runtime.InteropServices;

namespace OsuCursorWin;

/// <summary>
/// Win32 system tray icon implemented with the raw Shell_NotifyIcon API,
/// avoiding the WinForms NotifyIcon which needs a WinForms message loop.
/// Works directly under the WinUI 3 dispatcher message loop.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_SETVERSION = 0x00000004;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_GUID = 0x00000020;
    private const uint NIF_SHOWTIP = 0x40000000;
    private const int NOTIFYICON_VERSION_4 = 4;
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 100;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint IDM_SETTINGS = 1000;
    private const uint IDM_TOGGLE = 1001;
    private const uint IDM_EXIT = 1002;

    private static readonly Guid TRAY_GUID = new Guid("B1E2F3A4-5C6D-7E8F-9A0B-C1D2E3F4A5B6");
    private bool _disposed;
    private NOTIFYICONDATA _nid;
    private IntPtr _hIcon;
    private readonly IntPtr _hwnd;
    private System.Drawing.Icon? _icon;
    private bool _cursorEnabled = true;

    public event Action? ShowSettingsRequested;
    public event Action? ToggleCursorRequested;
    public event Action? ExitRequested;

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private static readonly WndProcDelegate _staticWndProc = WndProcImpl;
    private static TrayIcon? _activeInstance;

    public TrayIcon()
    {
        // Create a hidden message-only window to receive tray notifications
        var hInstance = GetModuleHandle(null);
        var className = "OsuCursorTray_" + Guid.NewGuid().ToString("N");

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_staticWndProc),
            hInstance = hInstance,
            lpszClassName = className
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, className, "OsuCursorTray", 0, 0, 0, 0, 0,
            new IntPtr(-3 /*HWND_MESSAGE*/), IntPtr.Zero, hInstance, IntPtr.Zero);

        // Load the app icon from embedded resource
        try
        {
            using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("OsuCursorWin.Images.AppIcon.ico");
            if (stream != null)
            {
                _icon = new System.Drawing.Icon(stream);
                _hIcon = _icon.Handle;
            }
        }
        catch { }
        if (_hIcon == IntPtr.Zero)
        {
            _hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512 /* IDI_APPLICATION */);
        }

        _activeInstance = this;

        _nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 0,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID | NIF_SHOWTIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = "osu! Cursor",
            guidItem = TRAY_GUID,
        };

        var ok = Shell_NotifyIcon(NIM_ADD, ref _nid);
        var err = Marshal.GetLastWin32Error();

        // Set version 4 for modern notification behavior (right-click context menu, balloon tips).
        // NIM_SETVERSION uses the uTimeoutOrVersion union field for the version number.
        var verNid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 0,
            uFlags = NIF_GUID,
            guidItem = TRAY_GUID,
            uTimeoutOrVersion = NOTIFYICON_VERSION_4
        };
        Shell_NotifyIcon(NIM_SETVERSION, ref verNid);

        AppLog.Log($"TrayIcon: NIM_ADD ok={ok} hwnd={_hwnd} hIcon={_hIcon} err={err}");
    }

    private static IntPtr LoadIconFromStream(System.IO.MemoryStream ms)
    {
        var icon = new System.Drawing.Icon(ms);
        // Keep the icon alive — returning icon.Handle would be destroyed
        // when the Icon is disposed. Store the handle and suppress GC.
        var h = icon.Handle;
        // Prevent GC by keeping the icon alive (GCHandle)
        // Actually, just return the handle; Shell_NotifyIcon copies it.
        return h;
    }

    private static IntPtr WndProcImpl(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = _activeInstance;
        if (self != null && msg == WM_TRAYICON)
        {
            var mouseMsg = lParam.ToInt32();
            if (mouseMsg == WM_LBUTTONDBLCLK)
            {
                self.ShowSettingsRequested?.Invoke();
                return IntPtr.Zero;
            }
            else if (mouseMsg == WM_RBUTTONUP || mouseMsg == WM_CONTEXTMENU)
            {
                self.ShowContextMenu();
                return IntPtr.Zero;
            }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, IDM_SETTINGS, "设置");
        AppendMenu(menu, 0, IDM_TOGGLE, _cursorEnabled ? "关闭光标" : "启用光标");
        AppendMenu(menu, 0x0800 /*MF_SEPARATOR*/, 0, null);
        AppendMenu(menu, 0, IDM_EXIT, "退出");

        GetCursorPos(out var pt);

        // VERSION_4 requires SetForegroundWindow before TrackPopupMenu so the
        // menu pops up at the cursor and dismisses correctly.
        SetForegroundWindow(_hwnd);
        var cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        // Post a benign message so Windows dismisses the menu cleanly.
        PostMessage(_hwnd, 0 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero);

        if (cmd == IDM_SETTINGS) ShowSettingsRequested?.Invoke();
        else if (cmd == IDM_TOGGLE)
        {
            _cursorEnabled = !_cursorEnabled;
            ToggleCursorRequested?.Invoke();
        }
        else if (cmd == IDM_EXIT) ExitRequested?.Invoke();

        DestroyMenu(menu);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shell_NotifyIcon(NIM_DELETE, ref _nid);
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        _activeInstance = null;
    }

    // --- P/Invoke ---

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr instance, string name);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(string sFile, int nIconIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIdNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved,
        IntPtr hWnd, IntPtr prcRect);
}