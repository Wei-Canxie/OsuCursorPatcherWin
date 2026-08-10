using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
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
    private readonly Canvas _cursorLayer;
    private readonly Grid _cursorRotate;
    private readonly Grid _cursorScale;
    private readonly RotateTransform _rotate;
    private readonly ScaleTransform _scale;
    private readonly Image _cursorImage;
    private readonly Image _additiveImage;
    private readonly NotifyIcon _trayIcon;
    private readonly TapSoundPlayer _tapSoundPlayer;
    private readonly TapSoundPlayer _hoverSoundPlayer;
    private SettingsWindow? _settingsWindow;
    private ToolStripMenuItem? _cursorToggleItem;
    private bool _cursorEnabled = true;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _topmostTimer;
    private DispatcherTimer? _fallbackTimer;

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
    private bool _wasHovering;
    private bool _wasHoverCandidate;
    private bool _wasResizePrompt;
    private IntPtr _baselineNormalHandle;
    private double _lastHoverSoundTime = double.NegativeInfinity;
    private bool _cursorInstalled;
    private bool _closing;
    private bool _forceTopmost = true;
    private IntPtr _lastCursorHandle;
    private int _lastWindowX = int.MinValue;
    private int _lastWindowY = int.MinValue;
    private int _lastWindowWidth;
    private int _lastWindowHeight;
    private IntPtr _mouseHook;
    private NativeMethods.LowLevelMouseProc? _mouseHookProc;
    private bool _mouseHookActive;
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

        Width = _cursorWindowSize;
        Height = _cursorWindowSize;
        Left = 0;
        Top = 0;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        _cursorScale = new Grid
        {
            Width = _cursorWidth,
            Height = _cursorHeight,
            RenderTransformOrigin = new Point(0, 0)
        };
        _scale = new ScaleTransform(1.0, 1.0);
        _cursorScale.RenderTransform = _scale;

        _cursorImage = LoadImageResource("OsuCursorWin.Images.cursor.png");
        _cursorScale.Children.Add(_cursorImage);

        _additiveImage = LoadImageResource("OsuCursorWin.Images.cursorAdditive.png");
        _additiveImage.Opacity = 0.0;
        _cursorScale.Children.Add(_additiveImage);

        _cursorRotate = new Grid
        {
            Width = _cursorWidth,
            Height = _cursorHeight,
            RenderTransformOrigin = new Point(0, 0)
        };
        _rotate = new RotateTransform(0.0);
        _cursorRotate.RenderTransform = _rotate;
        _cursorRotate.Children.Add(_cursorScale);

        _cursorLayer = new Canvas
        {
            IsHitTestVisible = false
        };
        _cursorLayer.Children.Add(_cursorRotate);
        Content = _cursorLayer;

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
            if (_hwnd != IntPtr.Zero && !_closing)
            {
                _cursorLayer.InvalidateVisual();
            }
        };
        _topmostTimer.Start();

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
        NativeMethods.SetClickThrough(_hwnd);
        UpdateCoordinateSystem();
        _forceTopmost = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateCoordinateSystem();

        if (!_cursorInstalled)
        {
            if (!CursorReplacer.Install())
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
        CompositionTarget.Rendering += OnRendering;
        _lastFrameTime = _clock.Elapsed.TotalSeconds;

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
        _settingsWindow?.ForceClose();
        CompositionTarget.Rendering -= OnRendering;
        UninstallMouseHook();
        _topmostTimer.Stop();
        CursorReplacer.Restore();

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
        _mouseHookProc = OnLowLevelMouse;
        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhMouseLl,
            _mouseHookProc,
            NativeMethods.GetModuleHandle(IntPtr.Zero),
            0);
        _mouseHookActive = _mouseHook != IntPtr.Zero;
        if (!_mouseHookActive)
        {
            Program.Log($"Mouse hook install failed: {Marshal.GetLastWin32Error()}");
            _fallbackTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(8)
            };
            _fallbackTimer.Tick += (_, _) => _cursorLayer.InvalidateVisual();
            _fallbackTimer.Start();
        }
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        _mouseHookActive = false;

        if (_fallbackTimer is not null)
        {
            _fallbackTimer.Stop();
            _fallbackTimer = null;
        }
    }

    private IntPtr OnLowLevelMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            switch ((uint)wParam.ToInt64())
            {
                case NativeMethods.WmMouseMove:
                    _cursorPoint = data.pt;
                    if (_hwnd != IntPtr.Zero)
                    {
                        UpdateWindowPosition();
                    }
                    break;
                case NativeMethods.WmLButtonDown:
                case NativeMethods.WmRButtonDown:
                case NativeMethods.WmMButtonDown:
                case NativeMethods.WmXButtonDown:
                    BeginPress(data.pt);
                    break;
                case NativeMethods.WmLButtonUp:
                case NativeMethods.WmRButtonUp:
                case NativeMethods.WmMButtonUp:
                case NativeMethods.WmXButtonUp:
                    EndPress();
                    break;
            }

            _cursorLayer.InvalidateVisual();
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
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
        TryBringAboveTaskbarPreview();
        UpdateVisual();
    }

    private void UpdateMouseState()
    {
        if (_mouseHookActive)
        {
            NativeMethods.GetCursorInfo(out var info);
            UpdatePointerState(info);
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
            UpdatePointerState(info);
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

    private void UpdatePointerState(NativeMethods.CURSORINFO info)
    {
        var normalHandle = CursorReplacer.GetBlankHandle(NativeMethods.OCR_NORMAL);
        var handHandle = CursorReplacer.GetBlankHandle(NativeMethods.OCR_HAND);

        if (info.hCursor != _lastCursorHandle)
        {
            _lastCursorHandle = info.hCursor;
            _forceTopmost = true;
            Program.Log(
                $"Cursor handle changed: current={info.hCursor.ToInt64():X} " +
                $"hand={handHandle.ToInt64():X} " +
                $"normal={normalHandle.ToInt64():X}");
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
            if (_baselineNormalHandle == IntPtr.Zero && info.hCursor != IntPtr.Zero && info.hCursor != handHandle)
            {
                _baselineNormalHandle = info.hCursor;
            }

            var isHoverCandidate = _pointerHover
                || (info.hCursor != IntPtr.Zero
                    && info.hCursor != normalHandle
                    && info.hCursor != _baselineNormalHandle);

            if (isHoverCandidate && !_wasHoverCandidate && !_mouseDown)
            {
                PlayHoverSample();
            }

            if (!isHoverCandidate)
            {
                _baselineNormalHandle = info.hCursor == normalHandle ? normalHandle : info.hCursor;
            }

            _wasHoverCandidate = isHoverCandidate;
            _wasHovering = _pointerHover;
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
    }

    private void EndPress()
    {
        if (!_mouseDown)
        {
            return;
        }

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
            _angleVelocity += (240.0 * angleDelta - 20.0 * _angleVelocity) * dt;
            _angle += _angleVelocity * dt;
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
        Canvas.SetLeft(_cursorRotate, _cursorWindowMargin);
        Canvas.SetTop(_cursorRotate, _cursorWindowMargin);

        _rotate.Angle = _angle;
        _scale.ScaleX = _scaleValue;
        _scale.ScaleY = _scaleValue;
        _additiveImage.Opacity = _additiveOpacity;

        UpdateWindowPosition();
    }

    private void UpdateWindowPosition()
    {
        var windowWidth = Math.Max(1, (int)Math.Ceiling(_cursorWindowSize * _dpiScaleX));
        var windowHeight = Math.Max(1, (int)Math.Ceiling(_cursorWindowSize * _dpiScaleY));
        var x = _cursorPoint.X - (int)Math.Round(_cursorWindowMargin * _dpiScaleX);
        var y = _cursorPoint.Y - (int)Math.Round(_cursorWindowMargin * _dpiScaleY);

        if (_hwnd != IntPtr.Zero
            && (_forceTopmost
                || x != _lastWindowX
                || y != _lastWindowY
                || windowWidth != _lastWindowWidth
                || windowHeight != _lastWindowHeight))
        {
            NativeMethods.MoveTopmost(_hwnd, x, y, windowWidth, windowHeight);
            _lastWindowX = x;
            _lastWindowY = y;
            _lastWindowWidth = windowWidth;
            _lastWindowHeight = windowHeight;
            _forceTopmost = false;
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

        return new NotifyIcon
        {
            Icon = ProgramIcon.CreateIcon(),
            Text = "osu! Cursor",
            Visible = true,
            ContextMenuStrip = menu
        };
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

        if (_cursorScale is not null)
        {
            _cursorScale.Width = _cursorWidth;
            _cursorScale.Height = _cursorHeight;
        }

        if (_cursorRotate is not null)
        {
            _cursorRotate.Width = _cursorWidth;
            _cursorRotate.Height = _cursorHeight;
        }

        if (_cursorImage is not null)
        {
            _cursorImage.Width = _cursorWidth;
            _cursorImage.Height = _cursorHeight;
            _additiveImage.Width = _cursorWidth;
            _additiveImage.Height = _cursorHeight;
            _settings.CursorWidth = width;
            _settings.Save();
        }

        _forceTopmost = true;
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
            CompositionTarget.Rendering += OnRendering;
            _lastFrameTime = _clock.Elapsed.TotalSeconds;
            _topmostTimer.Start();
            Show();
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
            UninstallMouseHook();
            _topmostTimer.Stop();
            CursorReplacer.Restore();
            Hide();
        }
    }

    private void ApplyAutoStart(bool enabled)
    {
        var applied = AutoStartManager.Apply(enabled);
        if (!applied)
        {
            Program.Log($"Failed to apply auto-start={enabled}");
            var reverted = !enabled;
            _settings.AutoStart = reverted;
            _settings.Save();
            _settingsWindow?.SetAutoStartChecked(reverted);
            return;
        }

        _settings.AutoStart = enabled;
        _settings.Save();
    }

    private void ApplyTapSound(bool enabled)
    {
        _settings.TapSoundEnabled = enabled;
        _tapSoundPlayer.Enabled = enabled;
        _settings.Save();
    }

    private void ApplyTapSoundVolume(double volume)
    {
        _settings.TapSoundVolume = Math.Clamp(volume, 0.0, 1.0);
        _settings.Save();
    }

    private void ApplyHoverSound(bool enabled)
    {
        _settings.HoverSoundEnabled = enabled;
        _hoverSoundPlayer.Enabled = enabled;
        _settings.Save();
    }

    private void ApplyHoverSoundVolume(double volume)
    {
        _settings.HoverSoundVolume = Math.Clamp(volume, 0.0, 1.0);
        _settings.Save();
    }

    private void ApplyHoverSoundMode(bool resizePrompt)
    {
        _settings.HoverSoundAsResizePrompt = resizePrompt;
        _settings.Save();
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
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualWidth = SystemParameters.VirtualScreenWidth;
        var xDip = _cursorPoint.X / _dpiScaleX;
        return Math.Clamp(((xDip - virtualLeft) / virtualWidth) * 2.0 - 1.0, -0.6, 0.6);
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
}
