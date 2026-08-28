using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace OsuCursorWin;

/// <summary>
/// Core rendering engine — drives the GdiCursorOverlay with mouse hook,
/// MMTimer-backed render loop, osu-style cursor animation, DC scene detection,
/// and cursor replacement.  Extracted from the WPF MainWindow; uses only
/// Win32 API and the WinUI 3 DispatcherQueue for UI-thread marshalling.
/// </summary>
internal sealed class CursorEngine : IDisposable
{
    // --- constants ---
    private const double BaseCursorWidth = 30.0;
    private const double BaseCursorHeight = 42.5;
    private const double PointerAngle = 24.3;
    private const double BaseCursorWindowSize = 160.0;
    private const double BaseCursorWindowMargin = 64.0;
    private const double MinCursorWidth = 16.0;
    private const double MaxCursorWidth = 64.0;
    private const int HotkeyToggleCursor = 1;
    private const int ModeSwitchDebounceMs = 90;

    // --- state ---
    private readonly AppSettings _settings;
    private readonly GdiCursorOverlay _overlay;
    private readonly TapSoundPlayer _tapSoundPlayer;
    private readonly TapSoundPlayer _hoverSoundPlayer;
    private readonly DispatcherQueue _dispatcher;
    private FileSystemWatcher? _settingsWatcher;
    private string? _settingsPath;
    private DateTime _lastSettingsReload = DateTime.MinValue;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _disposed;
    private bool _cursorEnabled = true;
    private bool _closing;

    // mouse hook
    private volatile IntPtr _mouseHook;
    private NativeMethods.LowLevelMouseProc? _mouseHookProc;
    private volatile bool _mouseHookActive;
    private Thread? _hookThread;
    private volatile bool _hookThreadRunning;
    private volatile uint _hookNativeThreadId;
    private volatile int _hookPosX, _hookPosY;
    private volatile int _hookDownX, _hookDownY;
    private volatile int _hookPressPending;
    private volatile int _hookReleasePending;

    // cursor state
    private NativeMethods.POINT _cursorPoint;
    private NativeMethods.POINT _downStart;
    private bool _mouseDown, _dragActive;
    private bool _pointerHover, _wasPointerHover, _wasHoverCandidate, _wasResizePrompt;
    private double _angle, _angleVelocity, _elasticStartAngle, _elasticDuration, _elasticElapsed;
    private bool _elasticReturning;
    private double _scaleValue = 1.0, _scaleVelocity;
    private double _additiveOpacity, _opacityVelocity;
    private double _lastFrameTime;
    private double _lastHoverSoundTime = double.NegativeInfinity;
    private int _renderTargetHz = 60;
    private double _renderIntervalMs = 16.67;
    private int _lastRefreshHz;
    private bool _cursorInstalled;
    private IntPtr _lastCursorHandle;
    private bool _cursorVisible = true;
    private bool _suppressCursor;
    private int _lastWindowX, _lastWindowY, _lastWindowWidth, _lastWindowHeight;
    private bool _forceTopmost = true;
    private int _topmostTick;
    private DateTime _lastTopmostReset = DateTime.MinValue;
    private DateTime _pendingOsuSince = DateTime.MinValue;
    private bool _pendingOsu;
    private DateTime _dbgNextLog = DateTime.MinValue;
    private readonly HashSet<string> _dcClassSeen = new();


    // topmost timer
    private NativeMethods.TimeProc? _topmostTimerCallback;
    private uint _topmostTimerId;

    // mm timer
    private NativeMethods.TimeProc? _mmCallback;
    private uint _mmTimerId;
    private int _renderQueued;
    private static bool _highResTimerEnabled;

    // perf diag
    private int _perfFrames;
    private double _perfTotalMs, _perfMouseMs, _perfAnimMs, _perfVisualMs, _perfTickMs;
    private double _perfNextLog;

    public CursorEngine(AppSettings settings, GdiCursorOverlay overlay,
        TapSoundPlayer tapPlayer, TapSoundPlayer hoverPlayer)
    {
        _settings = settings;
        _overlay = overlay;
        _tapSoundPlayer = tapPlayer;
        _hoverSoundPlayer = hoverPlayer;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        ApplyCursorDimensions(_settings.CursorWidth);
    }

    public void Start()
    {
        if (_closing) return;

        // Show overlay
        _overlay.ShowOverlay();

        // Install osu-style system cursor
        if (!_cursorInstalled)
        {
            using var osuImage = LoadBitmapResource("OsuCursorWin.Images.cursor.png");
            var osuSizePx = ComputeDcCursorSize();
            if (!CursorReplacer.Install(osuImage, osuSizePx,
                    _settings.DcAspectX, _settings.DcAspectY,
                    _settings.DcHotspotX, _settings.DcHotspotY))
            {
                CursorReplacer.Restore();
                AppLog.Log("Unable to install system cursor replacement.");
                return;
            }
            _cursorInstalled = true;
        }

        // Start mouse hook
        InstallMouseHook();

        // Enable high-res timer and start render loop
        EnableHighResTimer();
        ApplyRenderInterval();
        _lastFrameTime = _clock.Elapsed.TotalSeconds;
        StartMmTimer();

        // Start topmost maintainer
        StartTopmostTimer();

        // Watch settings file for changes (robust fallback)
        WatchSettingsFile();

        AppLog.Log("CursorEngine started.");
    }

    private void WatchSettingsFile()
    {
        try
        {
            _settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OsuCursorWin",
                "settings.json");
            if (!File.Exists(_settingsPath)) return;

            var dir = Path.GetDirectoryName(_settingsPath);
            if (string.IsNullOrEmpty(dir)) return;

            _settingsWatcher = new FileSystemWatcher(dir, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _settingsWatcher.Changed += OnSettingsFileChanged;
            _settingsWatcher.EnableRaisingEvents = true;
            AppLog.Log($"Watching settings file: {_settingsPath}");
        }
        catch (Exception ex)
        {
            AppLog.Log($"[DBG] WatchSettingsFile failed: {ex.Message}");
        }
    }

    private void OnSettingsFileChanged(object sender, FileSystemEventArgs e)
    {
        var now = DateTime.UtcNow;
        if (now - _lastSettingsReload < TimeSpan.FromMilliseconds(300)) return;
        _lastSettingsReload = now;
        _dispatcher.TryEnqueue(() =>
        {
            AppLog.Log("Settings file changed, reloading...");
            ReloadSettings();
        });
    }

    public void Stop()
    {
        _closing = true;
        StopMmTimer();
        StopTopmostTimer();
        UninstallMouseHook();
        DisableHighResTimer();
        CursorReplacer.Restore();
        _overlay.HideOverlay();
        _settings.Save();
        _settingsWatcher?.Dispose();
        _settingsWatcher = null;
        AppLog.Log("CursorEngine stopped.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _tapSoundPlayer.Dispose();
        _hoverSoundPlayer.Dispose();
    }

    // ======================== MOUSE HOOK ========================

    private void InstallMouseHook()
    {
        if (_hookThread is not null) return;

        if (NativeMethods.GetCursorInfo(out var ci))
        {
            _hookPosX = ci.ptScreenPos.X;
            _hookPosY = ci.ptScreenPos.Y;
            _cursorPoint = ci.ptScreenPos;
        }

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
            NativeMethods.PostThreadMessage(_hookNativeThreadId, NativeMethods.WmQuit, IntPtr.Zero, IntPtr.Zero);
        }
        if (_hookThread is not null)
        {
            if (!_hookThread.Join(1000))
                AppLog.Log("Hook thread did not exit within 1s; leaving it.");
            _hookThread = null;
        }
        _hookNativeThreadId = 0;
        _mouseHookActive = false;
    }

    private void HookThreadMain()
    {
        if (!_hookThreadRunning) return;
        _mouseHookProc = OnLowLevelMouse;
        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhMouseLl, _mouseHookProc,
            NativeMethods.GetModuleHandle(IntPtr.Zero), 0);
        _mouseHookActive = _mouseHook != IntPtr.Zero;
        if (!_mouseHookActive)
        {
            AppLog.Log($"Mouse hook install failed: {Marshal.GetLastWin32Error()}");
            return;
        }
        _hookNativeThreadId = NativeMethods.GetCurrentThreadId();
        AppLog.Log("Mouse hook running on dedicated thread.");
        if (!_hookThreadRunning)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            _mouseHookActive = false;
            return;
        }
        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
        NativeMethods.UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
        _mouseHookActive = false;
        AppLog.Log("Mouse hook thread exited.");
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

    // ======================== RENDER LOOP (MMTimer) ========================

    private int GetHighestRefreshRate()
    {
        const uint DisplayDeviceActive = 0x00000001;
        int highest = 0;
        for (uint i = 0; ; i++)
        {
            var dd = new NativeMethods.DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };
            if (!NativeMethods.EnumDisplayDevices(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & DisplayDeviceActive) == 0) continue;
            IntPtr dc = NativeMethods.CreateDC(null, dd.DeviceName, null, IntPtr.Zero);
            if (dc != IntPtr.Zero)
            {
                try { int hz = NativeMethods.GetDeviceCaps(dc, NativeMethods.VREFRESH); if (hz > highest) highest = hz; }
                finally { NativeMethods.DeleteDC(dc); }
            }
        }
        if (highest <= 1) highest = 60;
        return highest;
    }

    private void ApplyRenderInterval()
    {
        int hz = GetHighestRefreshRate();
        _lastRefreshHz = hz;
        _renderTargetHz = Math.Min(240, Math.Max(60, hz));
        _renderIntervalMs = 1000.0 / _renderTargetHz;
        if (_mmTimerId != 0) RestartMmTimerId();
        AppLog.Log($"[Display] render target -> {_renderTargetHz} Hz ({_renderIntervalMs:0.00} ms)");
    }

    private static void EnableHighResTimer()
    {
        if (_highResTimerEnabled) return;
        try { NativeMethods.timeBeginPeriod(1); _highResTimerEnabled = true; AppLog.Log("High-res timer enabled."); }
        catch (Exception ex) { AppLog.Log($"timeBeginPeriod failed: {ex.Message}"); }
    }

    private static void DisableHighResTimer()
    {
        if (!_highResTimerEnabled) return;
        try { NativeMethods.timeEndPeriod(1); _highResTimerEnabled = false; AppLog.Log("High-res timer disabled."); }
        catch (Exception ex) { AppLog.Log($"timeEndPeriod failed: {ex.Message}"); }
    }

    private void StartMmTimer()
    {
        if (_mmTimerId != 0) return;
        ApplyRenderInterval();
        RestartMmTimerId();
    }

    private void RestartMmTimerId()
    {
        if (_mmTimerId != 0) { NativeMethods.timeKillEvent(_mmTimerId); _mmTimerId = 0; }
        uint delay = (uint)Math.Max(1, Math.Round(_renderIntervalMs));
        _mmCallback = MmTimerCallback;
        _mmTimerId = NativeMethods.timeSetEvent(delay, 1, _mmCallback, IntPtr.Zero, NativeMethods.TimePeriodic);
    }

    private void StopMmTimer()
    {
        if (_mmTimerId != 0) { NativeMethods.timeKillEvent(_mmTimerId); _mmTimerId = 0; }
    }

    private void MmTimerCallback(uint uID, uint uMsg, IntPtr dwUser, IntPtr dw1, IntPtr dw2)
    {
        if (_closing) return;
        if (Interlocked.Exchange(ref _renderQueued, 1) == 1) return;
        _dispatcher.TryEnqueue(() =>
        {
            OnRendering();
            Volatile.Write(ref _renderQueued, 0);
        });
    }

    private void OnRendering()
    {
        if (_closing) return;
        var now = _clock.Elapsed.TotalSeconds;
        var dt = now - _lastFrameTime;
        _lastFrameTime = now;
        if (dt <= 0.0 || dt > 0.1) dt = 1.0 / 60.0;

        var t0 = _clock.Elapsed.TotalMilliseconds;
        UpdateMouseState();
        var t1 = _clock.Elapsed.TotalMilliseconds;
        UpdateAnimation(dt);
        var t2 = _clock.Elapsed.TotalMilliseconds;
        UpdateVisual();
        var t3 = _clock.Elapsed.TotalMilliseconds;

        _perfFrames++;
        _perfTotalMs += (t3 - t0); _perfMouseMs += (t1 - t0); _perfAnimMs += (t2 - t1); _perfVisualMs += (t3 - t2);
        _perfTickMs += (dt * 1000.0);
        if (_clock.Elapsed.TotalSeconds >= _perfNextLog)
        {
            _perfNextLog += 5.0;
            var f = _perfFrames > 0 ? _perfFrames : 1;
            long sb = _overlay._statSetBoundsCount, rc = _overlay._statRenderCount, uc = _overlay._statUlwCount;
            double sbMs = _overlay._statSetBoundsTicks / (double)Stopwatch.Frequency * 1000.0;
            double rMs = _overlay._statRenderTicks / (double)Stopwatch.Frequency * 1000.0;
            double uMs = _overlay._statUlwTicks / (double)Stopwatch.Frequency * 1000.0;
            AppLog.Log($"[PERF] frames={_perfFrames} avgTick={_perfTickMs / f:0.00}ms total={_perfTotalMs / f:0.00}ms mouse={_perfMouseMs / f:0.00}ms anim={_perfAnimMs / f:0.00}ms visual={_perfVisualMs / f:0.00}ms | overlay sb={sb}({sbMs:0.0}ms) render={rc}({rMs:0.0}ms) ulw={uc}({uMs:0.0}ms)");
            _perfFrames = 0; _perfTotalMs = 0; _perfMouseMs = 0; _perfAnimMs = 0; _perfVisualMs = 0; _perfTickMs = 0;
            _overlay._statSetBoundsCount = _overlay._statRenderCount = _overlay._statUlwCount = 0;
            _overlay._statSetBoundsTicks = _overlay._statRenderTicks = _overlay._statUlwTicks = 0;
        }
    }

    // ======================== TOPMOST MAINTAINER ========================

    private void StartTopmostTimer()
    {
        if (_topmostTimerId != 0) return;
        _topmostTimerCallback = TopmostTimerCallback;
        _topmostTimerId = NativeMethods.timeSetEvent(60, 60, _topmostTimerCallback, IntPtr.Zero, NativeMethods.TimePeriodic);
    }

    private void StopTopmostTimer()
    {
        if (_topmostTimerId != 0) { NativeMethods.timeKillEvent(_topmostTimerId); _topmostTimerId = 0; }
    }

    private void TopmostTimerCallback(uint uID, uint uMsg, IntPtr dwUser, IntPtr dw1, IntPtr dw2)
    {
        if (_closing) return;
        _dispatcher.TryEnqueue(() =>
        {
            if (_forceTopmost || ++_topmostTick >= 3)
            {
                _topmostTick = 0;
                _forceTopmost = false;
                _overlay.BringToTopmost();
                TryBringAboveTaskbarPreview();
            }
        });
    }

    // ======================== MOUSE STATE ========================

    private void UpdateMouseState()
    {
        if (_mouseHookActive)
        {
            _cursorPoint = new NativeMethods.POINT
            {
                X = Volatile.Read(ref _hookPosX),
                Y = Volatile.Read(ref _hookPosY)
            };
            ConsumeHookButtonEvents();
        }
        else
        {
            if (!NativeMethods.GetCursorInfo(out var info)) return;
            _cursorPoint = info.ptScreenPos;
            var pressed = (NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0;
            HandleButtonTransition(pressed);
        }

        if (NativeMethods.GetCursorInfo(out var cursorInfo))
            UpdatePointerState(cursorInfo);

        var winKeyPressed = (NativeMethods.GetAsyncKeyState(0x5B) & 0x8000) != 0
            || (NativeMethods.GetAsyncKeyState(0x5C) & 0x8000) != 0;
        if (winKeyPressed) _forceTopmost = true;

        if (_mouseDown && !_dragActive)
        {
            var dx = _cursorPoint.X - _downStart.X;
            var dy = _cursorPoint.Y - _downStart.Y;
            var threshold = _settings.CursorWidth;
            if (dx * dx + dy * dy > threshold * threshold) _dragActive = true;
        }
    }

    private void ConsumeHookButtonEvents()
    {
        if (Volatile.Read(ref _hookPressPending) != 0)
        {
            Volatile.Write(ref _hookPressPending, 0);
            _downStart = new NativeMethods.POINT { X = Volatile.Read(ref _hookDownX), Y = Volatile.Read(ref _hookDownY) };
            BeginPress(_downStart);
        }
        if (Volatile.Read(ref _hookReleasePending) != 0)
        {
            Volatile.Write(ref _hookReleasePending, 0);
            EndPress();
        }
    }

    private void HandleButtonTransition(bool pressed)
    {
        if (pressed) BeginPress(_cursorPoint);
        else EndPress();
    }

    private void BeginPress(NativeMethods.POINT point)
    {
        _elasticReturning = false;
        _mouseDown = true;
        _downStart = point;
        _dragActive = false;
        _forceTopmost = true;
        PlayTapSample(1.0);
        CursorReplacer.SetDragMode(true);
    }

    private void EndPress()
    {
        if (!_mouseDown) return;
        CursorReplacer.SetDragMode(false);
        if (_dragActive) StartElasticReturn();
        PlayTapSample(0.8);
        _mouseDown = false;
        _dragActive = false;
        _forceTopmost = true;
    }

    // ======================== POINTER STATE ========================

    private void UpdatePointerState(NativeMethods.CURSORINFO info)
    {
        var handHandle = CursorReplacer.GetBlankHandle(NativeMethods.OCR_HAND);
        var cursorShowing = (info.flags & NativeMethods.CursorShowing) != 0;
        _suppressCursor = info.hCursor != IntPtr.Zero
            && !NativeMethods.IsStandardCursor(info.hCursor)
            && !CursorReplacer.IsInstalledCursor(info.hCursor);
        var visible = cursorShowing && !_suppressCursor;

        var aboveDcSurface = IsOverDcSurface();
        var specialState = NativeMethods.IsStandardCursor(info.hCursor)
            && !NativeMethods.IsNormalArrowCursor(info.hCursor);
        var wantOsu = visible && (aboveDcSurface || specialState);

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

        SetCursorVisible(visible && !CursorReplacer.IsOsuMode());

        if (DateTime.UtcNow >= _dbgNextLog)
        {
            _dbgNextLog = DateTime.UtcNow.AddMilliseconds(500);
            AppLog.Log($"[DBG] ptrState hCursor=0x{info.hCursor.ToInt64():X} flags={info.flags} showing={cursorShowing} suppress={_suppressCursor} visible={visible} aboveDc={aboveDcSurface} osuMode={CursorReplacer.IsOsuMode()} pos=({_cursorPoint.X},{_cursorPoint.Y})");
        }

        if (info.hCursor != _lastCursorHandle)
        {
            _lastCursorHandle = info.hCursor;
            _forceTopmost = true;
        }

        _pointerHover = info.hCursor != IntPtr.Zero && info.hCursor == handHandle;

        if (_settings.HoverSoundAsResizePrompt)
        {
            var resize = IsResizeCursor();
            if (resize && !_wasResizePrompt && !_mouseDown) PlayHoverSample();
            _wasResizePrompt = resize;
        }
        else
        {
            var isHoverCandidate = IsHoverClickable();
            var handRisingEdge = _pointerHover && !_wasPointerHover && !_mouseDown;
            if (handRisingEdge || (isHoverCandidate && !_wasHoverCandidate && !_mouseDown)) PlayHoverSample();
            _wasPointerHover = _pointerHover;
            _wasHoverCandidate = isHoverCandidate;
        }
    }

    private bool IsHoverClickable()
    {
        // Fast path: hand cursor detection via cursor handle is the primary
        // mechanism (handled in UpdatePointerState). The UIA-based clickable
        // detection is omitted in the WinUI 3 port as it requires WPF
        // assemblies not available here.
        return false;
    }

    private void SetCursorVisible(bool visible)
    {
        if (_cursorVisible == visible) return;
        _cursorVisible = visible;
        if (visible) _overlay.ShowOverlay();
        else _overlay.HideOverlay();
        _forceTopmost = visible;
    }

    // ======================== ANIMATION ========================

    private void StartElasticReturn()
    {
        if (Math.Abs(_angle) < 0.5) return;
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
        if (t >= 1.0) { _angle = 0.0; _elasticReturning = false; _angleVelocity = 0.0; }
    }

    private static double ElasticOut(double t) =>
        Math.Pow(2.0, -10.0 * t) * Math.Sin((0.5 * t - 0.075) * 20.943951023931955) + 1.0 - 0.0004882812499999998 * t;

    private void UpdateAnimation(double dt)
    {
        double targetScale, targetAdditive;
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
        UpdateWindowPosition();
        _overlay.UpdateState(
            _lastWindowX, _lastWindowY, _lastWindowWidth, _lastWindowHeight,
            _angle, _scaleValue, _additiveOpacity, _cursorVisible,
            _settings.NormalAspectX, _settings.NormalAspectY,
            _settings.NormalHotspotX, _settings.NormalHotspotY);
    }

    private void UpdateWindowPosition()
    {
        var windowWidth = Math.Max(1, (int)Math.Ceiling(BaseCursorWindowSize));
        var windowHeight = Math.Max(1, (int)Math.Ceiling(BaseCursorWindowSize));
        var x = _cursorPoint.X - (int)Math.Round(BaseCursorWindowMargin);
        var y = _cursorPoint.Y - (int)Math.Round(BaseCursorWindowMargin);
        _lastWindowX = x; _lastWindowY = y;
        _lastWindowWidth = windowWidth; _lastWindowHeight = windowHeight;
        _forceTopmost = false;
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
        if (degrees > 180.0) degrees -= 360.0;
        else if (degrees < -180.0) degrees += 360.0;
        return degrees;
    }

    // ======================== DC SURFACE DETECTION ========================

    private bool IsOverDcSurface()
    {
        try
        {
            var hwnd = NativeMethods.WindowFromPoint(_cursorPoint);
            if (hwnd == IntPtr.Zero) return false;
            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            var className = sb.ToString();
            if (className.Length == 0) return false;

            var isDcClass = IsDcSurfaceClass(className);
            if (!isDcClass)
            {
                var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GaRoot);
                if (root != IntPtr.Zero && root != hwnd)
                {
                    var rsb = new StringBuilder(256);
                    NativeMethods.GetClassName(root, rsb, rsb.Capacity);
                    isDcClass = IsDcSurfaceClass(rsb.ToString());
                }
            }
            if (!isDcClass)
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out int pid);
                isDcClass = IsDcHostProcess(pid);
            }
            if (!isDcClass && _dcClassSeen.Add(className))
                AppLog.Log($"[Overlay] ptr window class='{className}' aboveDc=false (not matched)");
            if (isDcClass) return true;
            return IsWindowAboveCursor(_overlay.Handle, _cursorPoint);
        }
        catch (Exception ex) { AppLog.Log($"[Overlay] IsOverDcSurface exception: {ex.Message}"); return false; }
    }

    private bool IsWindowAboveCursor(IntPtr overlayHandle, NativeMethods.POINT pt)
    {
        var hwnd = overlayHandle;
        for (var i = 0; i < 40 && hwnd != IntPtr.Zero; i++)
        {
            hwnd = NativeMethods.GetWindow(hwnd, NativeMethods.GwHwndPrev);
            if (hwnd == IntPtr.Zero) break;
            if (!NativeMethods.IsWindowVisible(hwnd)) continue;
            if (NativeMethods.GetWindowRect(hwnd, out var rect) && PointInRect(pt, rect)) return true;
        }
        return false;
    }

    private static bool IsDcSurfaceClass(string className)
    {
        if (string.IsNullOrEmpty(className)) return false;
        return className.Contains("Windows.UI.Core.CoreWindow")
            || className.Contains("Windows.UI.Composition")
            || className.Contains("ApplicationFrameWindow")
            || className.Contains("XamlExplorerHostIslandWindow")
            || className.Contains("XamlIslandWindow")
            || className.Contains("Windows.UI.Core")
            || className.Contains("ControlCenterWindow")
            || className.Contains("Microsoft.UI.Content");
    }

    private static bool IsDcHostProcess(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var proc = Process.GetProcessById(pid);
            var name = proc.ProcessName;
            return name.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
                || name.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)
                || name.Equals("SearchHost", StringComparison.OrdinalIgnoreCase)
                || name.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase)
                || name.Equals("RuntimeBroker", StringComparison.OrdinalIgnoreCase)
                || name.Equals("LockApp", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Dwm", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ======================== PUBLIC SETTINGS API ========================

    /// <summary>Apply a cursor width change immediately (called from settings).</summary>
    public void ApplyCursorWidth(double width)
    {
        width = Math.Clamp(width, MinCursorWidth, MaxCursorWidth);
        _settings.CursorWidth = width;
        _overlay.Invalidate();
        _forceTopmost = true;
    }

    public AppSettings GetSettings() => _settings;

    /// <summary>Reload normal-scene tuning values from disk (called when the settings window edits NormalAspect/NormalHotspot).</summary>
    public void RefreshNormalSceneTuning()
    {
        _overlay.Invalidate();
    }

    /// <summary>Re-apply DC-scene cursor geometry after a settings change.</summary>
    public void ApplyDcSceneTuning()
    {
        if (!_cursorInstalled) return;
        var osuSizePx = ComputeDcCursorSize();
        CursorReplacer.Install(null, osuSizePx,
            _settings.DcAspectX, _settings.DcAspectY,
            _settings.DcHotspotX, _settings.DcHotspotY);
    }

    /// <summary>Reload settings from disk and reapply everything.</summary>
    public void ReloadSettings()
    {
        var fresh = AppSettings.Load();
        _settings.CursorWidth = fresh.CursorWidth;
        _settings.NormalAspectX = fresh.NormalAspectX;
        _settings.NormalAspectY = fresh.NormalAspectY;
        _settings.NormalHotspotX = fresh.NormalHotspotX;
        _settings.NormalHotspotY = fresh.NormalHotspotY;
        ApplyDcSceneTuning();
        _overlay.Invalidate();
        AppLog.Log($"ReloadSettings: CursorWidth={_settings.CursorWidth} NormalAspectX={_settings.NormalAspectX} NormalHotspotX={_settings.NormalHotspotX}");
    }

    /// <summary>Toggle cursor on/off.</summary>
    public void SetEnabled(bool enabled)
    {
        _cursorEnabled = enabled;
        if (enabled)
        {
            if (!_cursorInstalled) return;
            _overlay.ShowOverlay();
            InstallMouseHook();
            StartMmTimer();
            StartTopmostTimer();
        }
        else
        {
            StopMmTimer();
            StopTopmostTimer();
            UninstallMouseHook();
            CursorReplacer.Restore();
            _overlay.HideOverlay();
        }
    }

    // ======================== UTILITY ========================

    private void ApplyCursorDimensions(double width)
    {
        width = Math.Clamp(width, MinCursorWidth, MaxCursorWidth);
        _settings.CursorWidth = width;
    }

    private int ComputeDcCursorSize()
    {
        if (_settings.DcCursorSize > 0) return (int)Math.Clamp(_settings.DcCursorSize, 16, 96);
        return (int)Math.Clamp(Math.Round(BaseCursorWidth), 24, 96);
    }

    private void PlayTapSample(double baseFrequency)
    {
        if (!_cursorEnabled || !_tapSoundPlayer.Enabled || _settings.TapSoundVolume <= 0.0) return;
        var frequency = baseFrequency - 0.01 + Random.Shared.NextDouble() * 0.02;
        var volume = baseFrequency * _settings.TapSoundVolume;
        _tapSoundPlayer.Play(frequency, volume, GetCurrentBalance());
    }

    private void PlayHoverSample()
    {
        if (!_cursorEnabled || !_hoverSoundPlayer.Enabled || _settings.HoverSoundVolume <= 0.0) return;
        var now = _clock.Elapsed.TotalMilliseconds;
        if (now - _lastHoverSoundTime < 20.0) return;
        _lastHoverSoundTime = now;
        var frequency = 1.0 - 0.01 + Random.Shared.NextDouble() * 0.02;
        _hoverSoundPlayer.Play(frequency, _settings.HoverSoundVolume, GetCurrentBalance());
    }

    private double GetCurrentBalance()
    {
        var virtualLeft = NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen);
        var virtualWidth = NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen);
        if (virtualWidth <= 0) return 0.0;
        var x = _cursorPoint.X;
        return Math.Clamp(((x - virtualLeft) / (double)virtualWidth) * 2.0 - 1.0, -0.6, 0.6);
    }

    private bool IsResizeCursor()
    {
        var window = NativeMethods.WindowFromPoint(_cursorPoint);
        if (window == IntPtr.Zero) return false;
        var root = NativeMethods.GetAncestor(window, (uint)NativeMethods.GaRoot);
        if (root == IntPtr.Zero) return false;
        var style = NativeMethods.GetWindowStyle(root);
        if ((style & NativeMethods.WsMaximize) != 0 || (style & NativeMethods.WsThickFrame) == 0) return false;
        if (!NativeMethods.GetWindowRect(root, out var rect)) return false;
        var borderX = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SmCxSizeFrame));
        var borderY = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SmCySizeFrame));
        return _cursorPoint.X <= rect.Left + borderX || _cursorPoint.X >= rect.Right - borderX
            || _cursorPoint.Y <= rect.Top + borderY || _cursorPoint.Y >= rect.Bottom - borderY;
    }

    private void TryBringAboveTaskbarPreview()
    {
        var preview = NativeMethods.WindowFromPoint(_cursorPoint);
        var root = preview != IntPtr.Zero ? NativeMethods.GetAncestor(preview, (uint)NativeMethods.GaRoot) : IntPtr.Zero;
        if (IsTaskListThumbnail(root)) { NativeMethods.BringAbove(_overlay.Handle, root); return; }
        var found = NativeMethods.FindWindow("TaskListThumbnailWnd", null);
        while (found != IntPtr.Zero)
        {
            if (NativeMethods.GetWindowRect(found, out var rect) && PointInRect(_cursorPoint, rect))
            { NativeMethods.BringAbove(_overlay.Handle, found); return; }
            found = NativeMethods.GetWindow(found, (uint)NativeMethods.GwHwndNext);
        }
    }

    private static bool IsTaskListThumbnail(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var className = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        return className.ToString() == "TaskListThumbnailWnd";
    }

    private static bool PointInRect(NativeMethods.POINT point, NativeMethods.RECT rect) =>
        point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;

    private static byte[] LoadResourceBytes(string resourceName)
    {
        using var stream = typeof(CursorEngine).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing resource: {resourceName}");
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
}