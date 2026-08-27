using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;
using MessageBox = System.Windows.MessageBox;
using Point = System.Windows.Point;

namespace OsuCursorWin;

internal sealed class MainWindow : Window
{
    private const double BaseCursorWidth = 30.0;
    private const double BaseCursorHeight = 42.5;
    private const double PointerAngle = 24.3;
    private const double BaseCursorWindowSize = 160.0;
    private const double BaseCursorWindowMargin = 64.0;
    private const double MinCursorWidth = 16.0;
    private const double MaxCursorWidth = 64.0;

    private readonly AppSettings _settings;
    private readonly GdiCursorOverlay _overlay;
    private readonly NotifyIcon _trayIcon;
    private bool _overlayInitialized;
    private readonly TapSoundPlayer _tapSoundPlayer;
    private readonly TapSoundPlayer _hoverSoundPlayer;
    private SettingsWindow? _settingsWindow;
    private ToolStripMenuItem? _cursorToggleItem;
    private bool _cursorEnabled = true;
    private bool _cursorVisible = true;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private static bool _highResTimerEnabled;
    private readonly DispatcherTimer _topmostTimer;
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _settingsSaveDebounce;

    private NativeMethods.POINT _cursorPoint;
    private NativeMethods.POINT _downStart;
    private IntPtr _hwnd;
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;
    private double _lastFrameTime;
    private double _angle;
    private double _angleVelocity;
    private double _elasticStartAngle;
    private double _elasticDuration = 0.6;
    private double _elasticElapsed;
    private bool _elasticReturning;
    private double _scaleValue = 1.0;
    private double _scaleVelocity;
    private double _additiveOpacity;
    private double _opacityVelocity;
    private bool _mouseDown;
    private bool _dragActive;
    private bool _pointerHover;
    private bool _wasHoverCandidate;
    private bool _wasPointerHover;
    private bool _wasResizePrompt;
    private readonly Stopwatch _uiaThrottle = Stopwatch.StartNew();
    private IntPtr _lastUiaWindow = IntPtr.Zero;
    private IntPtr _lastUiaCursorHandle = IntPtr.Zero;
    private bool _lastUiaClickable;
    private double _lastHoverSoundTime = double.NegativeInfinity;
    private long _lastHookInvalidateTicks;
    private bool _cursorInstalled;
    private bool _closing;
    private bool _forceTopmost = true;
    private int _lastRefreshHz;
    private int _topmostTick;
    private IntPtr _lastCursorHandle;
    private int _lastWindowX = int.MinValue;
    private int _lastWindowY = int.MinValue;
    private int _lastWindowWidth;
    private int _lastWindowHeight;
    private volatile IntPtr _mouseHook;
    private NativeMethods.LowLevelMouseProc? _mouseHookProc;
    private volatile bool _mouseHookActive;
    private Thread? _hookThread;
    private volatile bool _hookThreadRunning;
    private volatile uint _hookNativeThreadId;
    private int _hookPosX;
    private int _hookPosY;
    private int _hookDownX;
    private int _hookDownY;
    private int _hookPressPending;
    private int _hookReleasePending;
    private IntPtr _lastZForegroundWindow = IntPtr.Zero;
    private IntPtr _hostileWindow = IntPtr.Zero;
    private bool _suppressCursor;
    private DateTime _dbgNextLog = DateTime.MinValue;
    // Debounce for system-cursor mode switching: rapid sweeps across many
    // special states (resize handles, links, text) or IME composition would
    // otherwise flip SetMode every frame, causing visible flicker.  We only
    // switch once the desired mode has been stable for this many ms.
    private bool _pendingOsu;
    private DateTime _pendingOsuSince = DateTime.MinValue;
    private const int ModeSwitchDebounceMs = 90;
    private const int HotkeyToggleCursor = 1;
    private double _cursorWidth;
    private double _cursorHeight;
    private double _cursorWindowSize;
    private double _cursorWindowMargin;
    private readonly bool _smoke;

    internal MainWindow(bool smoke = false)
    {
        _smoke = smoke;
        _settings = AppSettings.Load();
        _cursorWidth = Math.Clamp(_settings.CursorWidth, MinCursorWidth, MaxCursorWidth);
        ApplyCursorDimensions(_cursorWidth);

        Width = 1;
        Height = 1;
        Left = -32000;
        Top = -32000;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        Background = Brushes.Black;
        Topmost = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        _overlay = new GdiCursorOverlay();

        _tapSoundPlayer = new TapSoundPlayer(LoadResourceBytes("OsuCursorWin.Audio.cursorTap.wav"))
        {
            Enabled = _settings.TapSoundEnabled
        };
        _hoverSoundPlayer = new TapSoundPlayer(LoadResourceBytes("OsuCursorWin.Audio.defaultHover.wav"))
        {
            Enabled = _settings.HoverSoundEnabled
        };

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        DpiChanged += (_, _) => UpdateCoordinateSystem();

        _trayIcon = CreateTrayIcon();

        _topmostTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _topmostTimer.Tick += (_, _) =>
        {
            _forceTopmost = true;
            // Every 8 ticks (~2 s) re-check the display refresh rate so the
            // render interval adapts to dynamic refresh-rate changes.
            if (_topmostTick++ % 8 == 0)
            {
                int hz = GetHighestRefreshRate();
                if (hz != _lastRefreshHz)
                {
                    Program.Log($"[Display] refresh rate changed: {_lastRefreshHz} -> {hz} Hz");
                    _lastRefreshHz = hz;
                    ApplyRenderInterval();
                }
            }

            if (_overlayInitialized && !_closing)
            {
                _overlay.Invalidate();
                // Force the overlay back to the top of the topmost Z-order.  The
                // WinForms TopMost=true property only pins it once at creation;
                // Start menu / Action Center / clipboard / volume flyouts are
                // themselves topmost and would otherwise stack above the cursor.
                _overlay.BringToTopmost();
                TryBringAboveTaskbarPreview();
            }
        };
        _topmostTimer.Start();

        // Drive the render loop with a DispatcherTimer.  The WPF host window is
        // hidden, so CompositionTarget.Rendering no longer fires; a timer keeps
        // the overlay animating regardless.  The interval is set dynamically to
        // the highest display refresh rate (see ApplyRenderInterval) so the
        // overlay never wastes frames above the panel rate nor lags below it.
        _renderTimer = new DispatcherTimer();
        _renderTimer.Tick += OnRendering;
        ApplyRenderInterval();
        _renderTimer.Start();

        _settingsSaveDebounce = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _settingsSaveDebounce.Tick += (_, _) =>
        {
            _settingsSaveDebounce.Stop();
            _settings.Save();
        };

        if (smoke)
        {
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(2500)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Close();
            };
            timer.Start();
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        // The WPF host window stays hidden; the visible cursor is the WinForms
        // GdiCursorOverlay (which composites above DirectComposition surfaces).
        this.Hide();
        UpdateCoordinateSystem();
        _forceTopmost = true;
        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);
        NativeMethods.RegisterHotKey(_hwnd, HotkeyToggleCursor, NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModNoRepeat, NativeMethods.VkH);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateCoordinateSystem();

        if (!_overlayInitialized)
        {
            _overlayInitialized = true;
            _overlay.ShowOverlay();
        }

        if (!_cursorInstalled)
        {
            // Build an osu-style system cursor (a white ring) from the embedded
            // cursor image so the pointer stays visible over DirectComposition
            // surfaces (Start menu, Action Center, volume/clipboard flyouts)
            // that the animated overlay cannot cover.
            using var osuImage = LoadBitmapResource("OsuCursorWin.Images.cursor.png");
            var osuSizePx = ComputeDcCursorSize();
            if (!CursorReplacer.Install(osuImage, osuSizePx,
                    _settings.DcAspectX, _settings.DcAspectY,
                    _settings.DcHotspotX, _settings.DcHotspotY))
            {
                CursorReplacer.Restore();
                Program.Log("Unable to install system cursor replacement.");
                if (!_smoke)
                {
                    MessageBox.Show(
                        "无法替换系统光标，程序将退出。",
                        "osu! Cursor",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                Close();
                return;
            }

            _cursorInstalled = true;
        }

        _forceTopmost = true;
        InstallMouseHook();
        ApplyRenderInterval();
        _renderTimer.Start();
        _lastFrameTime = _clock.Elapsed.TotalSeconds;
        EnableHighResTimer();

        if (!_smoke)
        {
            ApplyAutoStart(_settings.AutoStart);
        }

        if (!_smoke && !AppSettings.Exists)
        {
            ShowSettingsWindow();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        DisableHighResTimer();
        _settingsWindow?.ForceClose();
        NativeMethods.UnregisterHotKey(_hwnd, HotkeyToggleCursor);
        _renderTimer.Stop();
        UninstallMouseHook();
        _topmostTimer.Stop();
        _settingsSaveDebounce.Stop();
        _settings.Save();
        CursorReplacer.Restore();
        _overlay?.Dispose();

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Icon?.Dispose();
            _trayIcon.Dispose();
        }

        _tapSoundPlayer.Dispose();
        _hoverSoundPlayer.Dispose();
    }

    private void UpdateCoordinateSystem()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _dpiScaleX = dpi.DpiScaleX;
        _dpiScaleY = dpi.DpiScaleY;
        _forceTopmost = true;
    }

    private void InstallMouseHook()
    {
        if (_hookThread is not null)
        {
            return;
        }

        // Seed the shared position so the cursor doesn't start at (0,0).
        if (NativeMethods.GetCursorInfo(out var ci))
        {
            _hookPosX = ci.ptScreenPos.X;
            _hookPosY = ci.ptScreenPos.Y;
            _cursorPoint = ci.ptScreenPos;
        }

        // Run the low-level mouse hook on its own thread with a dedicated message
        // pump. This decouples input capture from the UI thread so that a high
        // polling-rate mouse (>1000Hz) can no longer flood the WPF render loop
        // and cause cursor lag ("rubber-banding"), especially under CPU load.
        _hookThreadRunning = true;
        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "OsuCursorHook"
        };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
    }

    private void UninstallMouseHook()
    {
        _hookThreadRunning = false;
        if (_hookNativeThreadId != 0)
        {
            // Wake the hook thread's blocking message pump so it can exit cleanly.
            NativeMethods.PostThreadMessage(_hookNativeThreadId, NativeMethods.WmQuit, IntPtr.Zero, IntPtr.Zero);
        }

        if (_hookThread is not null)
        {
            if (!_hookThread.Join(1000))
            {
                Program.Log("Hook thread did not exit within 1s; leaving it (background thread).");
            }
            _hookThread = null;
        }

        _hookNativeThreadId = 0;
        _mouseHookActive = false;
    }

    private void HookThreadMain()
    {
        if (!_hookThreadRunning)
        {
            return;
        }

        _mouseHookProc = OnLowLevelMouse;
        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhMouseLl,
            _mouseHookProc,
            NativeMethods.GetModuleHandle(IntPtr.Zero),
            0);
        _mouseHookActive = _mouseHook != IntPtr.Zero;

        if (!_mouseHookActive)
        {
            Program.Log($"Mouse hook install failed (thread): {Marshal.GetLastWin32Error()}");
            return;
        }

        _hookNativeThreadId = NativeMethods.GetCurrentThreadId();
        Program.Log("Mouse hook running on dedicated thread.");

        if (!_hookThreadRunning)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            _mouseHookActive = false;
            return;
        }

        // Blocking message pump. GetMessage returns 0 on WM_QUIT (posted by
        // UninstallMouseHook), which terminates the loop and the thread.
        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        NativeMethods.UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
        _mouseHookActive = false;
        Program.Log("Mouse hook thread exited.");
    }

    private IntPtr OnLowLevelMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            switch ((uint)wParam.ToInt64())
            {
                case NativeMethods.WmMouseMove:
                    Volatile.Write(ref _hookPosX, data.pt.X);
                    Volatile.Write(ref _hookPosY, data.pt.Y);
                    break;
                case NativeMethods.WmLButtonDown:
                case NativeMethods.WmRButtonDown:
                case NativeMethods.WmMButtonDown:
                case NativeMethods.WmXButtonDown:
                    Volatile.Write(ref _hookDownX, data.pt.X);
                    Volatile.Write(ref _hookDownY, data.pt.Y);
                    Volatile.Write(ref _hookPressPending, 1);
                    break;
                case NativeMethods.WmLButtonUp:
                case NativeMethods.WmRButtonUp:
                case NativeMethods.WmMButtonUp:
                case NativeMethods.WmXButtonUp:
                    Volatile.Write(ref _hookReleasePending, 1);
                    break;
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>Enumerate every active display and return the highest refresh
    /// rate across all of them (multi-monitor users may move the cursor to the
    /// highest-Hz screen at any time, so we must not be limited by the primary
    /// display's rate).  Falls back to 60 if the API fails.</summary>
    private static int GetHighestRefreshRate()
    {
        const int EnumCurrentSettings = -1;
        const uint DisplayDeviceActive = 0x00000001;
        int highest = 0;

        // Walk the adapter chain via EnumDisplayDevices (each active adapter is
        // "\.\DISPLAY1", "\.\DISPLAY2", ...) and read its current mode.
        for (uint i = 0; ; i++)
        {
            var dd = new NativeMethods.DISPLAY_DEVICE { cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };
            if (!NativeMethods.EnumDisplayDevices(null, i, ref dd, 0))
            {
                break;
            }

            if ((dd.StateFlags & DisplayDeviceActive) == 0)
            {
                continue;
            }

            var dm = new NativeMethods.DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DEVMODE>() };
            if (NativeMethods.EnumDisplaySettings(dd.DeviceName, EnumCurrentSettings, ref dm)
                && dm.dmDisplayFrequency > (uint)highest)
            {
                highest = (int)dm.dmDisplayFrequency;
            }
        }

        // Normalise: some drivers report 0/1 (driver-chosen) — treat as 60.
        if (highest <= 1)
        {
            highest = 60;
        }

        Program.Log($"[Display] highest refresh rate across adapters: {highest} Hz");
        return highest;
    }

    /// <summary>Set the render DispatcherTimer interval to just below the
    /// highest display refresh rate so each frame aligns with a vsync slot
    /// (no wasted frames on slow panels, no under-render on fast ones).
    /// Called at startup and after any display-resolution change.</summary>
    private void ApplyRenderInterval()
    {
        int hz = GetHighestRefreshRate();
        _lastRefreshHz = hz;
        // Render slightly faster than the panel so a dropped tick never
        // starves a refresh, but clamp so we don't churn the CPU pointlessly.
        int targetHz = Math.Min(240, Math.Max(60, hz));
        _renderTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / targetHz);
        Program.Log($"[Display] render interval -> {targetHz} Hz ({_renderTimer.Interval.TotalMilliseconds:0.00} ms)");
    }

    /// <summary>Raise the Windows timer resolution to 1 ms so the render
    /// DispatcherTimer can actually fire at the display refresh rate.  Without
    /// this, SetTimer is clamped by the default ~15.6 ms system tick and the
    /// overlay runs at ~60 fps even on 144/180 Hz displays.</summary>
    private static void EnableHighResTimer()
    {
        if (_highResTimerEnabled)
        {
            return;
        }

        try
        {
            NativeMethods.timeBeginPeriod(1);
            _highResTimerEnabled = true;
            Program.Log("High-res timer enabled (timeBeginPeriod 1ms).");
        }
        catch (Exception ex)
        {
            Program.Log($"timeBeginPeriod failed: {ex.Message}");
        }
    }

    private static void DisableHighResTimer()
    {
        if (!_highResTimerEnabled)
        {
            return;
        }

        try
        {
            NativeMethods.timeEndPeriod(1);
            _highResTimerEnabled = false;
            Program.Log("High-res timer disabled.");
        }
        catch (Exception ex)
        {
            Program.Log($"timeEndPeriod failed: {ex.Message}");
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_closing || _hwnd == IntPtr.Zero)
        {
            return;
        }

        var now = _clock.Elapsed.TotalSeconds;
        var dt = now - _lastFrameTime;
        _lastFrameTime = now;
        if (dt <= 0.0 || dt > 0.1)
        {
            dt = 1.0 / 60.0;
        }

        UpdateMouseState();
        UpdateAnimation(dt);
        UpdateVisual();
    }

    private void UpdateMouseState()
    {
        if (_mouseHookActive)
        {
            // Read the latest position captured by the dedicated hook thread.
            _cursorPoint = new NativeMethods.POINT
            {
                X = Volatile.Read(ref _hookPosX),
                Y = Volatile.Read(ref _hookPosY)
            };

            ConsumeHookButtonEvents();
        }
        else
        {
            if (!NativeMethods.GetCursorInfo(out var info))
            {
                return;
            }

            _cursorPoint = info.ptScreenPos;
            var pressed = (NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0;
            HandleButtonTransition(pressed);
        }

        if (NativeMethods.GetCursorInfo(out var cursorInfo))
        {
            UpdatePointerState(cursorInfo);
        }

        var winKeyPressed = (NativeMethods.GetAsyncKeyState(0x5B) & 0x8000) != 0
            || (NativeMethods.GetAsyncKeyState(0x5C) & 0x8000) != 0;

        if (winKeyPressed)
        {
            _forceTopmost = true;
        }

        if (_mouseDown && !_dragActive)
        {
            var dx = _cursorPoint.X - _downStart.X;
            var dy = _cursorPoint.Y - _downStart.Y;
            var threshold = _cursorWidth * _dpiScaleX;
            if (dx * dx + dy * dy > threshold * threshold)
            {
                _dragActive = true;
            }
        }
    }

    private void ConsumeHookButtonEvents()
    {
        // Apply press/release events relayed from the hook thread on the UI thread
        // so all cursor animation state stays single-threaded.
        if (Volatile.Read(ref _hookPressPending) != 0)
        {
            Volatile.Write(ref _hookPressPending, 0);
            _downStart = new NativeMethods.POINT
            {
                X = Volatile.Read(ref _hookDownX),
                Y = Volatile.Read(ref _hookDownY)
            };
            BeginPress(_downStart);
        }

        if (Volatile.Read(ref _hookReleasePending) != 0)
        {
            Volatile.Write(ref _hookReleasePending, 0);
            EndPress();
        }
    }

    private void SetCursorVisible(bool visible)
    {
        if (_cursorVisible == visible)
        {
            return;
        }

        _cursorVisible = visible;
        if (_overlayInitialized && _overlay != null)
        {
            if (visible)
            {
                _overlay.ShowOverlay();
            }
            else
            {
                _overlay.HideOverlay();
            }
        }

        _forceTopmost = visible;
    }

    private void UpdatePointerState(NativeMethods.CURSORINFO info)
    {
        var handHandle = CursorReplacer.GetBlankHandle(NativeMethods.OCR_HAND);

        // Follow the system cursor's visibility. When the foreground app hides the
        // cursor (FPS gameplay, video players hiding the pointer, etc.) the
        // CURSOR_SHOWING flag is cleared; hide our overlay too so it never lingers
        // over content or draws a duplicate cursor.
        var cursorShowing = (info.flags & NativeMethods.CursorShowing) != 0;
        // When an app shows its own custom cursor (Snipaste capture/edit,
        // games, etc.), its handle is foreign to our installed blank cursors —
        // hide the osu overlay so the two cursors don't double-draw.
        // GetCursorInfo reports the handle the app requested. A standard system
        // cursor (arrow, I-beam, hand, ...) means normal operation — keep the
        // osu overlay. A custom handle (Snipaste capture/edit tools, games)
        // means the app is drawing its own cursor — hide the overlay so the two
        // cursors don't double-draw.
        // Foreign-cursor suppression: hide the overlay only for a genuinely
        // foreign cursor handle — neither a standard system cursor nor one of
        // our own installed blank/osu cursors.  (GetCursorInfo reports the
        // standard OCR handles even while our SetSystemCursor replacements are
        // active, so this keeps the overlay visible in normal scenes.)
        _suppressCursor = info.hCursor != IntPtr.Zero
            && !NativeMethods.IsStandardCursor(info.hCursor)
            && !CursorReplacer.IsInstalledCursor(info.hCursor);
        var visible = cursorShowing && !_suppressCursor;

        // Detect whether the pointer is over a DirectComposition XAML surface
        // (Start menu, Action Center, clipboard/volume flyouts) that the animated
        // overlay cannot composite above.  When it is, swap the system cursor to
        // the static osu ring so the pointer stays visible; otherwise keep it
        // blank and let the overlay provide the animated cursor.
        var aboveDcSurface = IsOverDcSurface();

        // Use the themed osu system cursor when (a) we're over a DirectComposition
        // surface where the overlay cannot composite, or (b) the pointer is in a
        // special state (resize handle, I-beam, hand, crosshair, ...) so the user
        // still gets the native "cursor changed" feedback near window edges and
        // over text/links instead of a frozen ring.  In the plain arrow state we
        // keep the system cursor blank and let the overlay animate.
        var specialState = NativeMethods.IsStandardCursor(info.hCursor)
            && !NativeMethods.IsNormalArrowCursor(info.hCursor);
        var wantOsu = visible && (aboveDcSurface || specialState);

        // Debounce: only actually switch SetMode when the desired state has
        // been stable for ~90ms.  This prevents rapid flicker when the mouse
        // sweeps across many special states (resize, hand, I-beam edges) or
        // IME composition briefly changes the cursor handle.
        if (wantOsu != _pendingOsu)
        {
            _pendingOsu = wantOsu;
            _pendingOsuSince = DateTime.UtcNow;
        }
        else if (wantOsu != CursorReplacer.IsOsuMode()
                 && (DateTime.UtcNow - _pendingOsuSince).TotalMilliseconds >= ModeSwitchDebounceMs)
        {
            CursorReplacer.SetMode(wantOsu);
        }

        // Overlay and osu system cursor are mutually exclusive: when the osu
        // system cursor is active (over a DC surface or special state) hide the
        // overlay — it is invisible there anyway and this avoids a double cursor
        // during the enter/leave transition.
        SetCursorVisible(visible && !CursorReplacer.IsOsuMode());

        if (DateTime.UtcNow >= _dbgNextLog)
        {
            _dbgNextLog = DateTime.UtcNow.AddMilliseconds(500);
            Program.Log($"[DBG] ptrState hCursor=0x{info.hCursor.ToInt64():X} flags={info.flags} showing={cursorShowing} suppress={_suppressCursor} visible={visible} aboveDc={aboveDcSurface} osuMode={CursorReplacer.IsOsuMode()} pos=({_cursorPoint.X},{_cursorPoint.Y})");
        }

        if (info.hCursor != _lastCursorHandle)
        {
            _lastCursorHandle = info.hCursor;
            _forceTopmost = true;
        }

        _pointerHover = info.hCursor != IntPtr.Zero
            && info.hCursor == handHandle;

        if (_settings.HoverSoundAsResizePrompt)
        {
            var resize = IsResizeCursor();
            if (resize && !_wasResizePrompt && !_mouseDown)
            {
                PlayHoverSample();
            }

            _wasResizePrompt = resize;
        }
        else
        {
            // Use Windows UI Automation to determine whether the element under
            // the cursor is truly clickable (Button, CheckBox, ListItem, etc.)
            // instead of guessing from cursor-handle heuristics. This eliminates
            // false hover triggers in Explorer, text fields, resize borders, etc.
            var isHoverCandidate = IsHoverClickable();

            // Fast path: hand cursor detection (info.hCursor == handHandle) is a
            // real-time OS signal that fires immediately — no UIA delay.  This
            // catches quick sweeps across clickable elements that UIA would miss
            // due to its cross-process COM latency (the "快速划过边缘线不响" bug).
            var handRisingEdge = _pointerHover && !_wasPointerHover && !_mouseDown;
            if (handRisingEdge || (isHoverCandidate && !_wasHoverCandidate && !_mouseDown))
            {
                PlayHoverSample();
            }

            _wasPointerHover = _pointerHover;
            _wasHoverCandidate = isHoverCandidate;
        }
    }

    private bool IsHoverClickable()
    {
        // UIA.FromPoint is a cross-process COM query; it must NOT run on every
        // mouse move. Only recompute when the window under the cursor changed,
        // when the system cursor handle changed (the OS signals the hover
        // context changed), or when a short throttle window has elapsed.
        var window = NativeMethods.WindowFromPoint(_cursorPoint);
        var contextChanged = window != _lastUiaWindow
            || _lastUiaCursorHandle != _lastCursorHandle;
        if (!contextChanged && _uiaThrottle.ElapsedMilliseconds < 25)
        {
            return _lastUiaClickable;
        }

        _lastUiaWindow = window;
        _lastUiaCursorHandle = _lastCursorHandle;
        _uiaThrottle.Restart();
        _lastUiaClickable = ComputeHoverClickable(_cursorPoint);
        return _lastUiaClickable;
    }

    private static bool ComputeHoverClickable(NativeMethods.POINT pt)
    {
        try
        {
            var element = AutomationElement.FromPoint(new Point(pt.X, pt.Y));
            if (element is null)
            {
                return false;
            }

            if (!element.Current.IsEnabled)
            {
                return false;
            }

            var type = element.Current.ControlType;
            if (type == ControlType.Button
                || type == ControlType.CheckBox
                || type == ControlType.RadioButton
                || type == ControlType.ComboBox
                || type == ControlType.ListItem
                || type == ControlType.MenuItem
                || type == ControlType.TabItem
                || type == ControlType.Hyperlink
                || type == ControlType.SplitButton
                || type == ControlType.Spinner
                || type == ControlType.DataItem)
            {
                return true;
            }

            // Many WPF/custom controls expose no specific ControlType (Custom);
            // treat them as clickable only when they accept keyboard focus.
            return type == ControlType.Custom && element.Current.IsKeyboardFocusable;
        }
        catch
        {
            // UIA provider failures (rare) degrade to "not clickable".
            return false;
        }
    }

    private void HandleButtonTransition(bool pressed)
    {
        if (pressed)
        {
            BeginPress(_cursorPoint);
        }
        else
        {
            EndPress();
        }
    }

    private void BeginPress(NativeMethods.POINT point)
    {
        _elasticReturning = false;
        _mouseDown = true;
        _downStart = point;
        _dragActive = false;
        _forceTopmost = true;
        PlayTapSample(1.0);
        // req3: when dragging, show hand.cur for the move cursor
        CursorReplacer.SetDragMode(true);
    }

    private void EndPress()
    {
        if (!_mouseDown)
        {
            return;
        }

        // req3: restore move.cur when drag ends
        CursorReplacer.SetDragMode(false);

        if (_dragActive)
        {
            StartElasticReturn();
        }

        PlayTapSample(0.8);
        _mouseDown = false;
        _dragActive = false;
        _forceTopmost = true;
    }

    private void StartElasticReturn()
    {
        if (Math.Abs(_angle) < 0.5)
        {
            return;
        }

        _elasticStartAngle = _angle;
        _elasticDuration = 0.6 * (1.0 + Math.Abs(_angle / 720.0));
        _elasticElapsed = 0.0;
        _elasticReturning = true;
        _angleVelocity = 0.0;
    }

    private void UpdateElasticReturn(double dt)
    {
        _elasticElapsed += dt;
        var t = Math.Min(1.0, _elasticElapsed / _elasticDuration);
        _angle = _elasticStartAngle * (1.0 - ElasticOut(t));

        if (t >= 1.0)
        {
            _angle = 0.0;
            _elasticReturning = false;
            _angleVelocity = 0.0;
        }
    }

    private static double ElasticOut(double t)
    {
        return Math.Pow(2.0, -10.0 * t)
            * Math.Sin((0.5 * t - 0.075) * 20.943951023931955)
            + 1.0
            - 0.0004882812499999998 * t;
    }

    private void UpdateAnimation(double dt)
    {
        double targetScale;
        double targetAdditive;

        if (_mouseDown)
        {
            targetAdditive = 1.0;
            targetScale = 0.9;
            var targetAngle = _dragActive ? CalculateDragAngle() : 0.0;
            var angleDelta = NormalizeAngle(targetAngle - _angle);
            _angle += angleDelta * Math.Clamp(dt * 8.0, 0.0, 1.0);
        }
        else if (_elasticReturning)
        {
            UpdateElasticReturn(dt);
            targetAdditive = 0.0;
            targetScale = 1.0;
        }
        else
        {
            var targetAngle = _pointerHover ? PointerAngle : 0.0;
            var angleDelta = NormalizeAngle(targetAngle - _angle);
            _angleVelocity += (240.0 * angleDelta - 20.0 * _angleVelocity) * dt;
            _angle += _angleVelocity * dt;
            targetAdditive = _pointerHover ? 1.0 : 0.0;
            targetScale = 1.0;
        }

        _scaleVelocity += (240.0 * (targetScale - _scaleValue) - 20.0 * _scaleVelocity) * dt;
        _scaleValue += _scaleVelocity * dt;

        _opacityVelocity += (160.0 * (targetAdditive - _additiveOpacity) - 18.0 * _opacityVelocity) * dt;
        _additiveOpacity += _opacityVelocity * dt;

        _scaleValue = Math.Clamp(_scaleValue, 0.8, 1.1);
        _additiveOpacity = Math.Clamp(_additiveOpacity, 0.0, 1.0);
    }

    private void UpdateVisual()
    {
        if (!_overlayInitialized || _overlay == null)
        {
            return;
        }

        UpdateWindowPosition();

        _overlay.UpdateState(
            _lastWindowX,
            _lastWindowY,
            _lastWindowWidth,
            _lastWindowHeight,
            _angle,
            _scaleValue,
            _additiveOpacity,
            _cursorVisible,
            _settings.NormalAspectX,
            _settings.NormalAspectY,
            _settings.NormalHotspotX,
            _settings.NormalHotspotY);
    }

    private void UpdateWindowPosition()
    {
        var windowWidth = Math.Max(1, (int)Math.Ceiling(_cursorWindowSize * _dpiScaleX));
        var windowHeight = Math.Max(1, (int)Math.Ceiling(_cursorWindowSize * _dpiScaleY));
        var x = _cursorPoint.X - (int)Math.Round(_cursorWindowMargin * _dpiScaleX);
        var y = _cursorPoint.Y - (int)Math.Round(_cursorWindowMargin * _dpiScaleY);

        // Clamp bounds so the overlay stays on-screen-ish; the GdiCursorOverlay
        // is a WinForms topmost form (non-layered, GDI-rendered) that composites
        // above all Windows 11 DirectComposition surfaces (Start menu, Action
        // Center, clipboard/volume flyouts).  Position is applied via UpdateState
        // in UpdateVisual.
        if (_overlayInitialized && _overlay != null)
        {
            _lastWindowX = x;
            _lastWindowY = y;
            _lastWindowWidth = windowWidth;
            _lastWindowHeight = windowHeight;
        }

        _forceTopmost = false;
        if (DateTime.UtcNow >= _dbgNextLog)
        {
            _dbgNextLog = DateTime.UtcNow.AddMilliseconds(500);
            Program.Log($"[DBG] winPos x={x} y={y} w={windowWidth} h={windowHeight} visible={_cursorVisible}");
        }
    }

    private double CalculateDragAngle()
    {
        var dx = _cursorPoint.X - _downStart.X;
        var dy = _cursorPoint.Y - _downStart.Y;
        return Math.Atan2(-dx, dy) * 180.0 / Math.PI + PointerAngle;
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

    private Image LoadImageResource(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource: {resourceName}");

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();

        var image = new Image
        {
            Source = bitmap,
            Width = _cursorWidth,
            Height = _cursorHeight,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false
        };

        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
        RenderOptions.SetEdgeMode(image, EdgeMode.Unspecified);
        return image;
    }

    private NotifyIcon CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();
        var settingsItem = new ToolStripMenuItem("设置");
        settingsItem.Click += (_, _) => ShowSettingsWindow();
        menu.Items.Add(settingsItem);

        _cursorToggleItem = new ToolStripMenuItem("关闭光标");
        _cursorToggleItem.Click += (_, _) => SetCursorEnabled(!_cursorEnabled);
        menu.Items.Add(_cursorToggleItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) =>
        {
            _settingsWindow?.ForceClose();
            Close();
        };
        menu.Items.Add(exitItem);

        var icon = new NotifyIcon
        {
            Icon = ProgramIcon.CreateIcon(),
            Text = "osu! Cursor",
            Visible = true,
            ContextMenuStrip = menu
        };

        // req 2c: double-click the tray icon opens the settings window directly.
        icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowSettingsWindow();
            }
        };

        return icon;
    }

    /// <summary>Re-apply DC-scene cursor geometry (size/aspect/hotspot) after a
    /// settings change.  Called from the settings window so tuning takes effect
    /// immediately without restarting.</summary>
    internal void ApplyDcSceneTuning()
    {
        if (!_cursorInstalled)
        {
            return;
        }

        var osuSizePx = ComputeDcCursorSize();
        CursorReplacer.Install(null, osuSizePx,
            _settings.DcAspectX, _settings.DcAspectY,
            _settings.DcHotspotX, _settings.DcHotspotY);
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings);
            _settingsWindow.CursorSizeChanged += ApplyCursorDimensions;
            _settingsWindow.AutoStartChanged += ApplyAutoStart;
            _settingsWindow.TapSoundChanged += ApplyTapSound;
            _settingsWindow.TapSoundVolumeChanged += ApplyTapSoundVolume;
            _settingsWindow.HoverSoundChanged += ApplyHoverSound;
            _settingsWindow.HoverSoundVolumeChanged += ApplyHoverSoundVolume;
            _settingsWindow.ResizeSoundModeChanged += ApplyHoverSoundMode;
            _settingsWindow.DcSceneTuningChanged += () => ApplyDcSceneTuning();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ApplyCursorDimensions(double width)
    {
        width = Math.Clamp(width, MinCursorWidth, MaxCursorWidth);
        _cursorWidth = width;
        _cursorHeight = width * (BaseCursorHeight / BaseCursorWidth);
        _cursorWindowSize = width * (BaseCursorWindowSize / BaseCursorWidth);
        _cursorWindowMargin = width * (BaseCursorWindowMargin / BaseCursorWidth);
        _settings.CursorWidth = width;
        ScheduleSave();
        _forceTopmost = true;
    }

    private int ComputeDcCursorSize()
    {
        // DC-scene size: DcCursorSize when explicitly set (px), else follow
        // CursorWidth (converted from DIP to physical px).
        if (_settings.DcCursorSize > 0)
        {
            return (int)Math.Clamp(_settings.DcCursorSize, 16, 96);
        }

        return (int)Math.Clamp(Math.Round(BaseCursorWidth * _dpiScaleX), 24, 96);
    }

    private void SetCursorEnabled(bool enabled)
    {
        if (_cursorEnabled == enabled)
        {
            return;
        }

        _cursorEnabled = enabled;
        if (_cursorToggleItem is not null)
        {
            _cursorToggleItem.Text = enabled ? "关闭光标" : "启用光标";
        }

        if (enabled)
        {
            if (!CursorReplacer.Install())
            {
                _cursorEnabled = false;
                if (_cursorToggleItem is not null)
                {
                    _cursorToggleItem.Text = "启用光标";
                }

                Program.Log("Failed to re-enable cursor replacement.");
                return;
            }

            _forceTopmost = true;
            InstallMouseHook();
            _renderTimer.Start();
            _lastFrameTime = _clock.Elapsed.TotalSeconds;
            _topmostTimer.Start();
            if (_overlayInitialized && _overlay != null)
            {
                _overlay.ShowOverlay();
            }
        }
        else
        {
            _renderTimer.Stop();
            UninstallMouseHook();
            _topmostTimer.Stop();
            CursorReplacer.Restore();
            if (_overlayInitialized && _overlay != null)
            {
                _overlay.HideOverlay();
            }
        }
    }

    private void ScheduleSave()
    {
        // Debounce settings writes: dragging a slider fires dozens of change events
        // per second; write to disk only after the user pauses (~400ms).
        if (_settingsSaveDebounce is null)
        {
            _settings.Save();
            return;
        }

        _settingsSaveDebounce.Stop();
        _settingsSaveDebounce.Start();
    }

    private void ApplyAutoStart(bool enabled)
    {
        var applied = AutoStartManager.Apply(enabled);
        if (!applied)
        {
            Program.Log($"Failed to apply auto-start={enabled}");
            var reverted = !enabled;
            _settings.AutoStart = reverted;
            ScheduleSave();
            _settingsWindow?.SetAutoStartChecked(reverted);
            return;
        }

        _settings.AutoStart = enabled;
        ScheduleSave();
    }

    private void ApplyTapSound(bool enabled)
    {
        _settings.TapSoundEnabled = enabled;
        _tapSoundPlayer.Enabled = enabled;
        ScheduleSave();
    }

    private void ApplyTapSoundVolume(double volume)
    {
        _settings.TapSoundVolume = Math.Clamp(volume, 0.0, 1.0);
        ScheduleSave();
    }

    private void ApplyHoverSound(bool enabled)
    {
        _settings.HoverSoundEnabled = enabled;
        _hoverSoundPlayer.Enabled = enabled;
        ScheduleSave();
    }

    private void ApplyHoverSoundVolume(double volume)
    {
        _settings.HoverSoundVolume = Math.Clamp(volume, 0.0, 1.0);
        ScheduleSave();
    }

    private void ApplyHoverSoundMode(bool resizePrompt)
    {
        _settings.HoverSoundAsResizePrompt = resizePrompt;
        ScheduleSave();
    }

    private void PlayTapSample(double baseFrequency)
    {
        if (!_cursorEnabled || !_tapSoundPlayer.Enabled)
        {
            return;
        }

        if (_settings.TapSoundVolume <= 0.0)
        {
            return;
        }

        var frequency = baseFrequency - 0.01 + Random.Shared.NextDouble() * 0.02;
        var volume = baseFrequency * _settings.TapSoundVolume;
        _tapSoundPlayer.Play(frequency, volume, GetCurrentBalance());
    }

    private void PlayHoverSample()
    {
        if (!_cursorEnabled || !_hoverSoundPlayer.Enabled || _settings.HoverSoundVolume <= 0.0)
        {
            return;
        }

        var now = _clock.Elapsed.TotalMilliseconds;
        if (now - _lastHoverSoundTime < 20.0)
        {
            return;
        }

        _lastHoverSoundTime = now;
        var frequency = 1.0 - 0.01 + Random.Shared.NextDouble() * 0.02;
        _hoverSoundPlayer.Play(frequency, _settings.HoverSoundVolume, GetCurrentBalance());
    }

    private double GetCurrentBalance()
    {
        // Use physical-pixel virtual-screen bounds (GetSystemMetrics) so we don't mix
        // DIP and physical coordinates. The mouse-hook _cursorPoint is already in physical
        // pixels, so this is a direct, consistent comparison.
        var virtualLeft = NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen);
        var virtualWidth = NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen);
        if (virtualWidth <= 0) return 0.0;
        var x = _cursorPoint.X;
        return Math.Clamp(((x - virtualLeft) / (double)virtualWidth) * 2.0 - 1.0, -0.6, 0.6);
    }

    private void InvalidateCursorVisualFromHook()
    {
        var now = _clock.ElapsedTicks;
        if (now - _lastHookInvalidateTicks >= TimeSpan.TicksPerMillisecond * 8)
        {
            _lastHookInvalidateTicks = now;
            _overlay?.Invalidate();
        }
    }

    private bool IsResizeCursor()
    {
        var window = NativeMethods.WindowFromPoint(_cursorPoint);
        if (window == IntPtr.Zero)
        {
            return false;
        }

        var root = NativeMethods.GetAncestor(window, (uint)NativeMethods.GaRoot);
        if (root == IntPtr.Zero || root == _hwnd)
        {
            return false;
        }

        var style = NativeMethods.GetWindowStyle(root);
        if ((style & NativeMethods.WsMaximize) != 0 || (style & NativeMethods.WsThickFrame) == 0)
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(root, out var rect))
        {
            return false;
        }

        var borderX = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SmCxSizeFrame));
        var borderY = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SmCySizeFrame));

        return _cursorPoint.X <= rect.Left + borderX
            || _cursorPoint.X >= rect.Right - borderX
            || _cursorPoint.Y <= rect.Top + borderY
            || _cursorPoint.Y >= rect.Bottom - borderY;
    }

    /// <summary>
    /// True when a visible window sits ABOVE our overlay in the Z-order at the
    /// cursor position — i.e. a DirectComposition XAML surface (Start menu,
    /// Action Center, volume/clipboard flyout) that the animated overlay cannot
    /// composite above.  We walk upward from the overlay (GW_HWNDPREV) and check
    /// whether any window there is visible and covers the cursor point.  This is
    /// more reliable than WindowFromPoint, whose result is ambiguous because the
    /// cursor lands in the ring's hollow centre (outside the overlay's clip
    /// region), so it can report the window below even in ordinary scenes.
    /// </summary>
    private bool IsWindowAboveCursor(IntPtr overlayHandle, NativeMethods.POINT pt)
    {
        var hwnd = overlayHandle;
        for (var i = 0; i < 40 && hwnd != IntPtr.Zero; i++)
        {
            hwnd = NativeMethods.GetWindow(hwnd, NativeMethods.GwHwndPrev);
            if (hwnd == IntPtr.Zero)
            {
                break;
            }

            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                continue;
            }

            if (NativeMethods.GetWindowRect(hwnd, out var rect) && PointInRect(pt, rect))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the pointer is over a DirectComposition XAML surface that the
    /// animated overlay cannot composite above (Start menu, Action Center,
    /// volume/clipboard flyouts, ...).  These are Windows.UI.Core.CoreWindow
    /// surfaces hosted by StartMenuExperienceHost / SearchHost / ShellExperienceHost,
    /// rendered by DirectComposition.  A plain Win32 Z-order walk from our overlay
    /// misses them because they are not ordinary top-level windows above us in the
    /// classic Z-order — so we ask the system directly what window is under the
    /// pointer and inspect its class name.
    /// </summary>
    private bool IsOverDcSurface()
    {
        if (!_overlayInitialized || _overlay == null)
        {
            return false;
        }

        try
        {
            var hwnd = NativeMethods.WindowFromPoint(_cursorPoint);
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            var className = sb.ToString();
            if (className.Length == 0)
            {
                return false;
            }

            // DirectComposition XAML surfaces share the Windows.UI.Core.CoreWindow
            // class.  Include the classic application-frame / XAML island classes
            // for safety.
            if (className.Contains("Windows.UI.Core.CoreWindow")
                || className.Contains("ApplicationFrameWindow")
                || className.Contains("Windows.UI.Composition"))
            {
                return true;
            }

            // Fallback: if the window under the pointer is above our overlay in the
            // classic Z-order it must be a surface that covers us.
            return IsWindowAboveCursor(_overlay.Handle, _cursorPoint);
        }
        catch (Exception ex)
        {
            Program.Log($"[Overlay] IsOverDcSurface exception: {ex.Message}");
            return false;
        }
    }

    private void TryBringAboveTaskbarPreview()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var preview = NativeMethods.WindowFromPoint(_cursorPoint);
        var root = preview != IntPtr.Zero
            ? NativeMethods.GetAncestor(preview, (uint)NativeMethods.GaRoot)
            : IntPtr.Zero;

        if (IsTaskListThumbnail(root))
        {
            NativeMethods.BringAbove(_hwnd, root);
            return;
        }

        var found = NativeMethods.FindWindow("TaskListThumbnailWnd", null);
        while (found != IntPtr.Zero)
        {
            if (NativeMethods.GetWindowRect(found, out var rect) && PointInRect(_cursorPoint, rect))
            {
                NativeMethods.BringAbove(_hwnd, found);
                return;
            }

            found = NativeMethods.GetWindow(found, (uint)NativeMethods.GwHwndNext);
        }
    }

    private static bool IsTaskListThumbnail(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var className = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        return className.ToString() == "TaskListThumbnailWnd";
    }

    private static bool PointInRect(NativeMethods.POINT point, NativeMethods.RECT rect)
    {
        return point.X >= rect.Left
            && point.X < rect.Right
            && point.Y >= rect.Top
            && point.Y < rect.Bottom;
    }

    private static byte[] LoadResourceBytes(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource: {resourceName}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static System.Drawing.Bitmap LoadBitmapResource(string resourceName)
    {
        var bytes = LoadResourceBytes(resourceName);
        using var ms = new MemoryStream(bytes);
        return new System.Drawing.Bitmap(ms);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmHotkey && wParam.ToInt32() == HotkeyToggleCursor)
        {
            _cursorEnabled = !_cursorEnabled;
            SetCursorEnabled(_cursorEnabled);
            handled = true;
        }

        return IntPtr.Zero;
    }

}
