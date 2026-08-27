using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using CheckBox = OsuCursorWin.OsuCheckbox;
using ComboBox = System.Windows.Controls.ComboBox;
using Image = System.Windows.Controls.Image;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Point = System.Windows.Point;

namespace OsuCursorWin;

/// <summary>
/// WinUI-3-styled settings window (hand-drawn, no Windows App SDK dependency).
/// Uses a left navigation rail + content cards, Mica-ish dark background,
/// rounded panels and WinUI-flavoured controls.  All geometry tuning for the
/// normal (overlay) and DC (system-cursor) scenes live here, plus a cursor
/// resource manager with preview / replace / reset-to-default.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(32, 33, 36));
    private static readonly Brush RailBrush = new SolidColorBrush(Color.FromRgb(24, 25, 28));
    private static readonly Brush PanelBrush = new SolidColorBrush(Color.FromRgb(41, 42, 46));
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(255, 102, 171));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(238, 239, 244));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(158, 160, 172));
    private static readonly Brush SelectedNavBrush = new SolidColorBrush(Color.FromRgb(58, 59, 64));

    private readonly AppSettings _settings;

    // --- cursor size (normal scene) ---
    private readonly Slider _sizeSlider;
    private readonly TextBlock _sizeValueText;

    // --- normal scene geometry ---
    private readonly Slider _nAspectXSlider;
    private readonly Slider _nAspectYSlider;
    private readonly Slider _nHotXSlider;
    private readonly Slider _nHotYSlider;
    private readonly TextBlock _nAspectXText;
    private readonly TextBlock _nAspectYText;
    private readonly TextBlock _nHotXText;
    private readonly TextBlock _nHotYText;
    private readonly Image _nPreview;

    // --- dc scene geometry ---
    private readonly Slider _dSizeSlider;
    private readonly Slider _dAspectXSlider;
    private readonly Slider _dAspectYSlider;
    private readonly Slider _dHotXSlider;
    private readonly Slider _dHotYSlider;
    private readonly TextBlock _dSizeText;
    private readonly TextBlock _dAspectXText;
    private readonly TextBlock _dAspectYText;
    private readonly TextBlock _dHotXText;
    private readonly TextBlock _dHotYText;
    private readonly Image _dPreview;

    // --- sound ---
    private readonly CheckBox _tapSoundCheckBox;
    private readonly CheckBox _hoverSoundCheckBox;
    private readonly CheckBox _resizeSoundCheckBox;
    private readonly Slider _tapSoundVolumeSlider;
    private readonly TextBlock _tapSoundVolumeText;
    private readonly Slider _hoverSoundVolumeSlider;
    private readonly TextBlock _hoverSoundVolumeText;

    // --- system ---
    private readonly CheckBox _autoStartCheckBox;

    // --- cursor resource manager (req 2b) ---
    private readonly ComboBox _resourceCombo;
    private readonly Image _resourcePreview;
    private readonly TextBlock _resourceStatus;

    private bool _allowClose;
    // Cache built pages so shared control fields are never re-attached
    // (re-building a page would throw "already the logical child").
    private readonly List<UIElement?> _cachedPages = new();
    private bool _autoStartUpdating;
    private bool _tapSoundUpdating;
    private bool _hoverSoundUpdating;
    private bool _resizeSoundUpdating;

    internal event Action<double>? CursorSizeChanged;
    internal event Action<bool>? AutoStartChanged;
    internal event Action<bool>? TapSoundChanged;
    internal event Action<double>? TapSoundVolumeChanged;
    internal event Action<bool>? HoverSoundChanged;
    internal event Action<double>? HoverSoundVolumeChanged;
    internal event Action<bool>? ResizeSoundModeChanged;
    /// <summary>Fired when DC-scene cursor geometry (size/aspect/hotspot) changes.</summary>
    internal event Action? DcSceneTuningChanged;

    internal SettingsWindow(AppSettings settings)
    {
        _settings = settings;

        Title = "osu! Cursor 设置";
        Width = 760;
        Height = 560;
        MinWidth = 680;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        Icon = ProgramIcon.CreateWindowIcon();
        Background = BackgroundBrush;
        Foreground = TextBrush;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        UseLayoutRounding = true;

        // Pre-create controls used by both the pages and public accessors.
        _sizeSlider = new Slider { Minimum = 16, Maximum = 64, TickFrequency = 1, IsSnapToTickEnabled = true, Value = settings.CursorWidth };
        StyleSlider(_sizeSlider);
        _sizeSlider.ValueChanged += OnSizeSliderChanged;
        _sizeValueText = new TextBlock { Text = $"{settings.CursorWidth:0} px", Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center };

        _nAspectXSlider = MakeTuneSlider(0.5, 1.5, settings.NormalAspectX);
        _nAspectYSlider = MakeTuneSlider(0.5, 1.5, settings.NormalAspectY);
        _nHotXSlider = MakeTuneSlider(-40, 40, settings.NormalHotspotX);
        _nHotYSlider = MakeTuneSlider(-40, 40, settings.NormalHotspotY);
        _nAspectXText = new TextBlock { Text = settings.NormalAspectX.ToString("0.00"), Foreground = TextBrush };
        _nAspectYText = new TextBlock { Text = settings.NormalAspectY.ToString("0.00"), Foreground = TextBrush };
        _nHotXText = new TextBlock { Text = $"{settings.NormalHotspotX:0}", Foreground = TextBrush };
        _nHotYText = new TextBlock { Text = $"{settings.NormalHotspotY:0}", Foreground = TextBrush };
        _nPreview = MakePreviewImage(LoadPng("OsuCursorWin.Images.cursor.png"), 130);
        _nAspectXSlider.ValueChanged += (_, _) => { _settings.NormalAspectX = _nAspectXSlider.Value; _nAspectXText.Text = _settings.NormalAspectX.ToString("0.00"); ScheduleSave(); };
        _nAspectYSlider.ValueChanged += (_, _) => { _settings.NormalAspectY = _nAspectYSlider.Value; _nAspectYText.Text = _settings.NormalAspectY.ToString("0.00"); ScheduleSave(); };
        _nHotXSlider.ValueChanged += (_, _) => { _settings.NormalHotspotX = _nHotXSlider.Value; _nHotXText.Text = $"{_settings.NormalHotspotX:0}"; ScheduleSave(); };
        _nHotYSlider.ValueChanged += (_, _) => { _settings.NormalHotspotY = _nHotYSlider.Value; _nHotYText.Text = $"{_settings.NormalHotspotY:0}"; ScheduleSave(); };

        _dSizeSlider = MakeTuneSlider(16, 96, settings.DcCursorSize > 0 ? settings.DcCursorSize : 32);
        _dAspectXSlider = MakeTuneSlider(0.5, 1.5, settings.DcAspectX);
        _dAspectYSlider = MakeTuneSlider(0.5, 1.5, settings.DcAspectY);
        _dHotXSlider = MakeTuneSlider(-40, 40, settings.DcHotspotX);
        _dHotYSlider = MakeTuneSlider(-40, 40, settings.DcHotspotY);
        _dSizeText = new TextBlock { Text = $"{_dSizeSlider.Value:0} px", Foreground = TextBrush };
        _dAspectXText = new TextBlock { Text = settings.DcAspectX.ToString("0.00"), Foreground = TextBrush };
        _dAspectYText = new TextBlock { Text = settings.DcAspectY.ToString("0.00"), Foreground = TextBrush };
        _dHotXText = new TextBlock { Text = $"{settings.DcHotspotX:0}", Foreground = TextBrush };
        _dHotYText = new TextBlock { Text = $"{settings.DcHotspotY:0}", Foreground = TextBrush };
        _dPreview = MakePreviewImage(LoadPng("OsuCursorWin.Images.cursor.png"), 130);
        _dSizeSlider.ValueChanged += (_, _) => { _settings.DcCursorSize = _dSizeSlider.Value; _dSizeText.Text = $"{_dSizeSlider.Value:0} px"; ScheduleSave(); DcSceneTuningChanged?.Invoke(); };
        _dAspectXSlider.ValueChanged += (_, _) => { _settings.DcAspectX = _dAspectXSlider.Value; _dAspectXText.Text = _settings.DcAspectX.ToString("0.00"); ScheduleSave(); DcSceneTuningChanged?.Invoke(); };
        _dAspectYSlider.ValueChanged += (_, _) => { _settings.DcAspectY = _dAspectYSlider.Value; _dAspectYText.Text = _settings.DcAspectY.ToString("0.00"); ScheduleSave(); DcSceneTuningChanged?.Invoke(); };
        _dHotXSlider.ValueChanged += (_, _) => { _settings.DcHotspotX = _dHotXSlider.Value; _dHotXText.Text = $"{_dHotXSlider.Value:0}"; ScheduleSave(); DcSceneTuningChanged?.Invoke(); };
        _dHotYSlider.ValueChanged += (_, _) => { _settings.DcHotspotY = _dHotYSlider.Value; _dHotYText.Text = $"{_dHotYSlider.Value:0}"; ScheduleSave(); DcSceneTuningChanged?.Invoke(); };

        _tapSoundCheckBox = new CheckBox { Content = "敲击音效", IsChecked = settings.TapSoundEnabled, Foreground = TextBrush, FontSize = 13 };
        _tapSoundCheckBox.Checked += OnTapSoundChecked;
        _tapSoundCheckBox.Unchecked += OnTapSoundChecked;
        _tapSoundVolumeText = new TextBlock { Text = $"{Math.Round(settings.TapSoundVolume * 100):0}%", Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center };
        _tapSoundVolumeSlider = new Slider { Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true, Value = Math.Clamp(settings.TapSoundVolume * 100, 0, 100), VerticalAlignment = VerticalAlignment.Center };
        StyleSlider(_tapSoundVolumeSlider);
        _tapSoundVolumeSlider.ValueChanged += OnTapSoundVolumeChanged;

        _hoverSoundCheckBox = new CheckBox { Content = "悬停音效", IsChecked = settings.HoverSoundEnabled, Foreground = TextBrush, FontSize = 13 };
        _hoverSoundCheckBox.Checked += OnHoverSoundChecked;
        _hoverSoundCheckBox.Unchecked += OnHoverSoundChecked;
        _hoverSoundVolumeText = new TextBlock { Text = $"{Math.Round(settings.HoverSoundVolume * 100):0}%", Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center };
        _hoverSoundVolumeSlider = new Slider { Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true, Value = Math.Clamp(settings.HoverSoundVolume * 100, 0, 100), VerticalAlignment = VerticalAlignment.Center };
        StyleSlider(_hoverSoundVolumeSlider);
        _hoverSoundVolumeSlider.ValueChanged += OnHoverSoundVolumeChanged;

        _resizeSoundCheckBox = new CheckBox { Content = "窗口拉伸时播放", IsChecked = settings.HoverSoundAsResizePrompt, Foreground = TextBrush, FontSize = 13 };
        _resizeSoundCheckBox.Checked += OnResizeSoundModeChecked;
        _resizeSoundCheckBox.Unchecked += OnResizeSoundModeChecked;

        _autoStartCheckBox = new CheckBox { Content = "开机自启", IsChecked = settings.AutoStart, Foreground = TextBrush, FontSize = 13 };
        _autoStartCheckBox.Checked += OnAutoStartChecked;
        _autoStartCheckBox.Unchecked += OnAutoStartChecked;

        _resourceCombo = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        _resourcePreview = MakePreviewImage(LoadPng("OsuCursorWin.Images.cursor.png"), 80);
        _resourceStatus = new TextBlock { Text = "", Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap };

        // ---- Left navigation rail + right content host ----
        var navItems = new (string label, Func<UIElement> factory)[]
        {
            ("光标", () => BuildCursorPage()),
            ("场景对齐", () => BuildSceneComparePage()),
            ("音效", () => BuildSoundPage()),
            ("光标资源", () => BuildResourcePage()),
            ("系统", () => BuildSystemPage()),
        };

        var rail = new StackPanel { Width = 160, Background = RailBrush };
        var railButtons = new List<Button>();
        var contentHost = new ContentControl
        {
            Margin = new Thickness(24, 20, 24, 20),
            VerticalContentAlignment = VerticalAlignment.Stretch
        };

        for (int i = 0; i < navItems.Length; i++)
        {
            var idx = i;
            var btn = new Button
            {
                Content = navItems[i].label,
                Height = 40,
                Margin = new Thickness(8, i == 0 ? 20 : 2, 8, 2),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(16, 0, 0, 0),
                Background = i == 0 ? SelectedNavBrush : Brushes.Transparent,
                Foreground = TextBrush,
                BorderThickness = new Thickness(0),
                FontSize = 14,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btn.Click += (_, _) =>
            {
                foreach (var b in railButtons) { b.Background = Brushes.Transparent; }
                btn.Background = SelectedNavBrush;
                contentHost.Content = GetCachedPage(idx, navItems[idx].factory);
            };
            railButtons.Add(btn);
            rail.Children.Add(btn);
        }

        contentHost.Content = GetCachedPage(0, navItems[0].factory);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(rail, 0);
        Grid.SetColumn(contentHost, 1);
        grid.Children.Add(rail);
        grid.Children.Add(contentHost);



        Content = grid;

        Loaded += (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
        };

        StateChanged += OnStateChanged;
        Closing += OnClosing;
    }

    // ======================================================================
    // Pages
    // ======================================================================

    private UIElement GetCachedPage(int index, Func<UIElement> factory)
    {
        while (_cachedPages.Count <= index)
        {
            _cachedPages.Add(null);
        }

        if (_cachedPages[index] is null)
        {
            _cachedPages[index] = factory();
        }

        return _cachedPages[index]!;
    }

    private UIElement BuildCursorPage()
    {
        var cursorValuePanel = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_sizeValueText, Dock.Right);
        cursorValuePanel.Children.Add(_sizeValueText);
        cursorValuePanel.Children.Add(new TextBlock { Text = "16 - 64", Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center });

        var resetButton = new Button { Content = "恢复默认", Width = 96, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0), Background = AccentBrush, Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(12, 6, 12, 6) };
        resetButton.Click += (_, _) => _sizeSlider.Value = 30;
        AttachElasticButton(resetButton);

        var section = new StackPanel();
        section.Children.Add(cursorValuePanel);
        section.Children.Add(_sizeSlider);
        section.Children.Add(resetButton);
        return CreateSection("光标", section);
    }

    private UIElement BuildSceneComparePage()
    {
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = BuildSceneCompareContent() };
        return scroll;
    }

    private UIElement BuildSceneCompareContent()
    {
        var root = new StackPanel();

        var normalPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        normalPanel.Children.Add(CreateSceneHeader("普通场景", "半透明动画光标（overlay）", _nPreview));
        normalPanel.Children.Add(CreateTuneRow("宽长比", _nAspectXSlider, _nAspectXText));
        normalPanel.Children.Add(CreateTuneRow("高长比", _nAspectYSlider, _nAspectYText));
        normalPanel.Children.Add(CreateTuneRow("定位 X", _nHotXSlider, _nHotXText));
        normalPanel.Children.Add(CreateTuneRow("定位 Y", _nHotYSlider, _nHotYText));
        root.Children.Add(CreateSection("普通场景 (overlay)", normalPanel));

        var dcPanel = new StackPanel();
        dcPanel.Children.Add(CreateSceneHeader("DC 场景", "系统光标（开始菜单等）", _dPreview));
        dcPanel.Children.Add(CreateTuneRow("大小", _dSizeSlider, _dSizeText));
        dcPanel.Children.Add(CreateTuneRow("宽长比", _dAspectXSlider, _dAspectXText));
        dcPanel.Children.Add(CreateTuneRow("高长比", _dAspectYSlider, _dAspectYText));
        dcPanel.Children.Add(CreateTuneRow("定位 X", _dHotXSlider, _dHotXText));
        dcPanel.Children.Add(CreateTuneRow("定位 Y", _dHotYSlider, _dHotYText));
        root.Children.Add(CreateSection("DC 场景 (系统光标)", dcPanel));

        var resetBoth = new Button { Content = "恢复两组默认", Width = 150, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0), Background = AccentBrush, Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(12, 6, 12, 6) };
        resetBoth.Click += (_, _) =>
        {
            _nAspectXSlider.Value = 1.0; _nAspectYSlider.Value = 1.0;
            _nHotXSlider.Value = 0; _nHotYSlider.Value = 0;
            _dSizeSlider.Value = 32; _dAspectXSlider.Value = 1.0; _dAspectYSlider.Value = 1.0;
            _dHotXSlider.Value = 0; _dHotYSlider.Value = 0;
            ScheduleSave();
            DcSceneTuningChanged?.Invoke();
        };
        AttachElasticButton(resetBoth);
        root.Children.Add(resetBoth);

        return root;
    }

    private UIElement BuildSoundPage()
    {
        var soundSection = new StackPanel();
        soundSection.Children.Add(_tapSoundCheckBox);
        soundSection.Children.Add(CreateSliderRow("音量", _tapSoundVolumeSlider, _tapSoundVolumeText));
        soundSection.Children.Add(_hoverSoundCheckBox);
        soundSection.Children.Add(CreateSliderRow("悬停音量", _hoverSoundVolumeSlider, _hoverSoundVolumeText));
        soundSection.Children.Add(_resizeSoundCheckBox);
        return CreateSection("音效", soundSection);
    }

    private UIElement BuildResourcePage()
    {
        var section = new StackPanel();

        var header = new TextBlock { Text = "选择要替换的系统光标，然后点击下方按钮。替换文件（.cur / .ani）会被复制到用户目录并立即生效。", TextWrapping = TextWrapping.Wrap, Foreground = MutedBrush, Margin = new Thickness(0, 0, 0, 10) };
        section.Children.Add(header);

        section.Children.Add(_resourceCombo);
        var previewRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 12) };
        previewRow.Children.Add(_resourcePreview);
        previewRow.Children.Add(new TextBlock { Text = "当前资源预览", Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
        section.Children.Add(previewRow);

        var btnRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        var replaceBtn = new Button { Content = "替换所选光标…", Width = 150, Margin = new Thickness(0, 0, 10, 0), Background = AccentBrush, Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(12, 6, 12, 6) };
        replaceBtn.Click += OnReplaceResourceClicked;
        AttachElasticButton(replaceBtn);

        var resetOneBtn = new Button { Content = "重置所选", Width = 110, Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(12, 6, 12, 6) };
        resetOneBtn.Click += OnResetOneClicked;
        AttachElasticButton(resetOneBtn);

        var resetAllBtn = new Button { Content = "全部重置为默认", Width = 140, Padding = new Thickness(12, 6, 12, 6) };
        resetAllBtn.Click += OnResetAllClicked;
        AttachElasticButton(resetAllBtn);

        btnRow.Children.Add(replaceBtn);
        btnRow.Children.Add(resetOneBtn);
        btnRow.Children.Add(resetAllBtn);
        section.Children.Add(btnRow);

        section.Children.Add(_resourceStatus);

        _resourceCombo.SelectionChanged += OnResourceSelectionChanged;
        PopulateResourceCombo();
        return CreateSection("光标资源", section);
    }

    private UIElement BuildSystemPage()
    {
        var systemSection = new StackPanel();
        systemSection.Children.Add(_autoStartCheckBox);
        return CreateSection("系统", systemSection);
    }

    // ======================================================================
    // Resource manager (req 2b)
    // ======================================================================

    private readonly record struct ResourceEntry(string Label, uint Id);

    private void PopulateResourceCombo()
    {
        _resourceCombo.Items.Clear();
        _resourceCombo.Items.Add(new ResourceEntry("普通箭头 (OCR_NORMAL)", NativeMethods.OCR_NORMAL));
        _resourceCombo.Items.Add(new ResourceEntry("文本选择 I 型 (OCR_IBEAM)", NativeMethods.OCR_IBEAM));
        _resourceCombo.Items.Add(new ResourceEntry("忙碌/等待 (OCR_WAIT)", NativeMethods.OCR_WAIT));
        _resourceCombo.Items.Add(new ResourceEntry("应用启动中 (OCR_APPSTARTING)", NativeMethods.OCR_APPSTARTING));
        _resourceCombo.Items.Add(new ResourceEntry("十字准星 (OCR_CROSS)", NativeMethods.OCR_CROSS));
        _resourceCombo.Items.Add(new ResourceEntry("链接/按钮 (OCR_HAND)", NativeMethods.OCR_HAND));
        _resourceCombo.Items.Add(new ResourceEntry("移动 (OCR_SIZEALL)", NativeMethods.OCR_SIZEALL));
        _resourceCombo.Items.Add(new ResourceEntry("不可用 (OCR_NO)", NativeMethods.OCR_NO));
        _resourceCombo.Items.Add(new ResourceEntry("帮助 (OCR_HELP)", NativeMethods.OCR_HELP));
        _resourceCombo.SelectedIndex = 0;
    }

    private void OnResourceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_resourceCombo.SelectedItem is ResourceEntry entry)
        {
            var custom = CursorReplacer.GetCustomCursorPathFor(entry.Id);
            _resourceStatus.Text = custom != null
                ? $"当前使用自定义资源：{Path.GetFileName(custom)}"
                : "当前使用内置默认资源。";
            RefreshResourcePreview(entry.Id);
        }
    }

    private void OnReplaceResourceClicked(object sender, RoutedEventArgs e)
    {
        if (_resourceCombo.SelectedItem is not ResourceEntry entry)
        {
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择光标文件 (.cur / .ani)",
            Filter = "光标文件 (*.cur;*.ani)|*.cur;*.ani|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                CursorReplacer.SetCustomCursor(entry.Id, dlg.FileName);
                _resourceStatus.Text = $"已替换为：{Path.GetFileName(dlg.FileName)}（立即生效）";
                RefreshResourcePreview(entry.Id);
                ScheduleSave();
            }
            catch (Exception ex)
            {
                _resourceStatus.Text = $"替换失败：{ex.Message}";
            }
        }
    }

    private void OnResetOneClicked(object sender, RoutedEventArgs e)
    {
        if (_resourceCombo.SelectedItem is not ResourceEntry entry)
        {
            return;
        }

        CursorReplacer.ResetCustomCursor(entry.Id);
        CursorReplacer.Reload();
        _resourceStatus.Text = "已重置为内置默认资源。";
        RefreshResourcePreview(entry.Id);
        ScheduleSave();
    }

    private void OnResetAllClicked(object sender, RoutedEventArgs e)
    {
        CursorReplacer.ResetAllCustomCursors();
        CursorReplacer.Reload();
        _resourceStatus.Text = "已全部重置为内置默认资源。";
        RefreshResourcePreview(_resourceCombo.SelectedItem is ResourceEntry entry ? entry.Id : NativeMethods.OCR_NORMAL);
        ScheduleSave();
    }

    private void RefreshResourcePreview(uint id)
    {
        // Try the user's custom cursor override first, then the embedded
        // resource for this OCR ID.  Render the actual cursor bitmap so the
        // preview shows what will really appear on screen.
        try
        {
            var customPath = CursorReplacer.GetCustomCursorPathFor(id);
            if (customPath != null && File.Exists(customPath))
            {
                using var bmp = LoadCursorPreview(customPath);
                if (bmp != null)
                {
                    _resourcePreview.Source = ToBitmapSource(bmp);
                    return;
                }
            }

            var filename = CursorReplacer.GetEmbeddedCursorFilename(id);
            if (filename != null)
            {
                var resName = filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    ? "OsuCursorWin.Images." + filename
                    : "OsuCursorWin.Cursors." + filename;
                using var stream = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(resName);
                if (stream != null)
                {
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    var bytes = ms.ToArray();

                    System.Drawing.Bitmap? bmp = filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                        ? new System.Drawing.Bitmap(new MemoryStream(bytes))
                        : LoadCursorPreviewFromBytes(bytes, Path.GetExtension(filename));

                    if (bmp != null)
                    {
                        _resourcePreview.Source = ToBitmapSource(bmp);
                        bmp.Dispose();
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Program.Log($"[Settings] preview failed id={id}: {ex.Message}");
        }

        // Fallback: the normal cursor image.
        var fb = LoadPng("OsuCursorWin.Images.cursor.png");
        _resourcePreview.Source = ToBitmapSource(fb);
        fb.Dispose();
    }

    /// <summary>Render the first frame of a .cur/.ani cursor file as a bitmap.</summary>
    private static System.Drawing.Bitmap? LoadCursorPreview(string path)
    {
        var hcur = NativeMethods.LoadCursorFromFile(path);
        if (hcur == IntPtr.Zero) return null;
        try
        {
            if (!NativeMethods.GetIconInfo(hcur, out var info)) return null;
            try
            {
                if (info.hbmColor == IntPtr.Zero)
                {
                    // Monochrome mask-only cursor; fall back to the mask as ARGB.
                    if (info.hbmMask == IntPtr.Zero) return null;
                    return System.Drawing.Image.FromHbitmap(info.hbmMask);
                }
                return System.Drawing.Image.FromHbitmap(info.hbmColor);
            }
            finally
            {
                if (info.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(info.hbmColor);
                if (info.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(info.hbmMask);
            }
        }
        finally
        {
            NativeMethods.DestroyCursor(hcur);
        }
    }

    private static System.Drawing.Bitmap? LoadCursorPreviewFromBytes(byte[] data, string ext)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "OsuCursorPreview_" + Guid.NewGuid().ToString("N") + ext);
        try
        {
            File.WriteAllBytes(tmp, data);
            return LoadCursorPreview(tmp);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    // ======================================================================
    // Public accessors (used by MainWindow)
    // ======================================================================

    internal void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    internal void SetAutoStartChecked(bool enabled)
    {
        _autoStartUpdating = true;
        _autoStartCheckBox.IsChecked = enabled;
        _autoStartUpdating = false;
    }

    internal void SetTapSoundChecked(bool enabled)
    {
        _tapSoundUpdating = true;
        _tapSoundCheckBox.IsChecked = enabled;
        _tapSoundUpdating = false;
    }

    internal void SetHoverSoundChecked(bool enabled)
    {
        _hoverSoundUpdating = true;
        _hoverSoundCheckBox.IsChecked = enabled;
        _hoverSoundUpdating = false;
    }

    internal void SetResizeSoundModeChecked(bool enabled)
    {
        _resizeSoundUpdating = true;
        _resizeSoundCheckBox.IsChecked = enabled;
        _resizeSoundUpdating = false;
    }

    // ======================================================================
    // Handlers
    // ======================================================================

    private void OnSizeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var width = Math.Round(e.NewValue);
        _settings.CursorWidth = width;
        _sizeValueText.Text = $"{width:0} px";
        CursorSizeChanged?.Invoke(width);
    }

    private void OnAutoStartChecked(object? sender, RoutedEventArgs e)
    {
        if (_autoStartUpdating) { return; }
        AutoStartChanged?.Invoke(_autoStartCheckBox.IsChecked == true);
    }

    private void OnTapSoundChecked(object? sender, RoutedEventArgs e)
    {
        if (_tapSoundUpdating) { return; }
        TapSoundChanged?.Invoke(_tapSoundCheckBox.IsChecked == true);
    }

    private void OnTapSoundVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var percent = Math.Round(e.NewValue);
        _tapSoundVolumeText.Text = $"{percent:0}%";
        TapSoundVolumeChanged?.Invoke(percent / 100.0);
    }

    private void OnHoverSoundChecked(object? sender, RoutedEventArgs e)
    {
        if (_hoverSoundUpdating) { return; }
        HoverSoundChanged?.Invoke(_hoverSoundCheckBox.IsChecked == true);
    }

    private void OnHoverSoundVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var percent = Math.Round(e.NewValue);
        _hoverSoundVolumeText.Text = $"{percent:0}%";
        HoverSoundVolumeChanged?.Invoke(percent / 100.0);
    }

    private void OnResizeSoundModeChecked(object? sender, RoutedEventArgs e)
    {
        if (_resizeSoundUpdating) { return; }
        ResizeSoundModeChanged?.Invoke(_resizeSoundCheckBox.IsChecked == true);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
            Hide();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    // ======================================================================
    // UI helpers
    // ======================================================================

    private static Slider MakeTuneSlider(double min, double max, double value)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), TickFrequency = 1, IsSnapToTickEnabled = false };
        StyleSlider(s);
        return s;
    }

    private static Border CreateSection(string title, params UIElement[] children)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = AccentBrush, Margin = new Thickness(0, 0, 0, 12) });
        foreach (var child in children)
        {
            stack.Children.Add(child);
        }

        return new Border
        {
            Background = PanelBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18),
            Margin = new Thickness(0, 0, 0, 12),
            Child = stack
        };
    }

    private static UIElement CreateSceneHeader(string title, string subtitle, Image preview)
    {
        var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(preview);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        texts.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = TextBrush });
        texts.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = MutedBrush, Margin = new Thickness(0, 3, 0, 0) });
        panel.Children.Add(texts);
        return panel;
    }

    private static DockPanel CreateTuneRow(string label, Slider slider, TextBlock? valueText)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (valueText != null)
        {
            valueText.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(valueText, Dock.Right);
            panel.Children.Add(valueText);
        }

        var labelText = new TextBlock { Text = label, Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(labelText, Dock.Left);

        slider.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(labelText);
        panel.Children.Add(slider);
        return panel;
    }

    private static DockPanel CreateSliderRow(string label, Slider slider, TextBlock valueText)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        valueText.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(valueText, Dock.Right);
        var labelText = new TextBlock { Text = label, Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(labelText, Dock.Left);
        slider.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(valueText);
        panel.Children.Add(labelText);
        panel.Children.Add(slider);
        return panel;
    }

    private static void StyleSlider(Slider slider)
    {
        slider.Foreground = AccentBrush;
        slider.MinHeight = 24;
        slider.Margin = new Thickness(0, 4, 0, 0);
    }

    private static Image MakePreviewImage(Bitmap bmp, int size)
    {
        var src = ToBitmapSource(bmp);
        bmp.Dispose();
        var img = new Image
        {
            Source = src,
            Width = size,
            Height = size * 442 / 312,
            Stretch = Stretch.Uniform
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return img;
    }

    private static void AttachElasticButton(Button button)
    {
        var scale = new ScaleTransform(1, 1);
        button.RenderTransform = scale;
        button.RenderTransformOrigin = new Point(0.5, 0.5);
        button.MouseEnter += (_, _) => AnimateScale(scale, 1.04, 120, new QuinticEase { EasingMode = EasingMode.EaseOut });
        button.MouseLeave += (_, _) => AnimateScale(scale, 1.0, 180, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 });
        button.PreviewMouseLeftButtonDown += (_, _) => AnimateScale(scale, 0.96, 90, new QuinticEase { EasingMode = EasingMode.EaseOut });
        button.PreviewMouseLeftButtonUp += (_, _) => AnimateScale(scale, 1.0, 160, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 });
    }

    private static void AnimateScale(ScaleTransform transform, double to, int milliseconds, IEasingFunction? easing)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = easing };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private static System.Drawing.Bitmap LoadPng(string resourceName)
    {
        using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource: {resourceName}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;
        return new System.Drawing.Bitmap(ms);
    }

    private static BitmapSource ToBitmapSource(System.Drawing.Bitmap bmp)
    {
        var hbmp = bmp.GetHbitmap();
        try
        {
            return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            NativeMethods.DeleteObject(hbmp);
        }
    }

    private DateTime _lastSave = DateTime.MinValue;

    private void ScheduleSave()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastSave).TotalMilliseconds >= 300)
        {
            _settings.Save();
            _lastSave = now;
        }
    }
}
