using System;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using WinRT.Interop;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OsuCursorWin;

/// <summary>
/// WinUI 3 settings window built in code with real Fluent/WinUI controls:
/// NavigationView, TextBox (with +/- buttons), ToggleSwitch.
/// All settings use the shared AppSettings instance from the engine.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly CursorEngine? _engine;

    public SettingsWindow(CursorEngine? engine = null)
    {
        _engine = engine;
        _settings = engine?.GetSettings() ?? AppSettings.Load();
        Title = "osu! Cursor 设置";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(960, 680));

        // Intercept WM_CLOSE: hide the window instead of destroying it so the
        // engine + tray keep running in the background.
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            AppWindow.Hide();
        };

        var root = new Grid();
        var nav = new NavigationView
        {
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            OpenPaneLength = 200,
        };

        nav.MenuItems.Add(new NavigationViewItem
        {
            Content = "外观",
            Icon = new SymbolIcon(Symbol.View),
            Tag = "appearance"
        });
        nav.MenuItems.Add(new NavigationViewItem
        {
            Content = "光标",
            Icon = new SymbolIcon(Symbol.Target),
            Tag = "cursor"
        });
        nav.MenuItems.Add(new NavigationViewItem
        {
            Content = "场景对齐",
            Icon = new SymbolIcon(Symbol.AlignCenter),
            Tag = "align"
        });
        nav.MenuItems.Add(new NavigationViewItem
        {
            Content = "音效",
            Icon = new SymbolIcon(Symbol.Audio),
            Tag = "sound"
        });
        nav.MenuItems.Add(new NavigationViewItem
        {
            Content = "系统",
            Icon = new SymbolIcon(Symbol.Setting),
            Tag = "system"
        });

        nav.SelectionChanged += (s, e) =>
        {
            if (nav.SelectedItem is NavigationViewItem item && item.Tag is string tag)
                nav.Content = BuildPage(tag);
        };

        // NavigationView must be attached to the visual tree before setting
        // SelectedItem, otherwise it throws COMException (0x80070490).
        nav.Loaded += (_, _) =>
        {
            try
            {
                nav.SelectedItem = nav.MenuItems[0];
            }
            catch (Exception ex)
            {
                AppLog.Log($"nav.Loaded set SelectedItem failed: {ex.Message}");
            }
            // Apply appearance settings when window first shows
            ApplyAppearance();
        };

        root.Children.Add(nav);
        Content = root;
    }

    private object BuildPage(string tag)
    {
        return tag switch
        {
            "appearance" => BuildAppearancePage(),
            "cursor" => BuildCursorPage(),
            "align" => BuildAlignPage(),
            "sound" => BuildSoundPage(),
            "system" => BuildSystemPage(),
            _ => new TextBlock { Text = tag }
        };
    }

    private FrameworkElement BuildAppearancePage()
    {
        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(24, 16, 24, 16) };
        panel.Children.Add(Header("外观设置"));

        // Theme selection
        panel.Children.Add(new TextBlock { Text = "主题", FontWeight = FontWeights.SemiBold });
        var themePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };

        var themeFollowRadio = new RadioButton { Content = "跟随系统", Tag = "follow" };
        var themeLightRadio = new RadioButton { Content = "亮色", Tag = "light" };
        var themeDarkRadio = new RadioButton { Content = "暗色", Tag = "dark" };

        switch (_settings.Theme)
        {
            case AppSettings.ThemeMode.Light: themeLightRadio.IsChecked = true; break;
            case AppSettings.ThemeMode.Dark: themeDarkRadio.IsChecked = true; break;
            default: themeFollowRadio.IsChecked = true; break;
        }

        themeFollowRadio.Checked += (_, _) => { _settings.Theme = AppSettings.ThemeMode.FollowSystem; ApplyAppearance(); };
        themeLightRadio.Checked += (_, _) => { _settings.Theme = AppSettings.ThemeMode.Light; ApplyAppearance(); };
        themeDarkRadio.Checked += (_, _) => { _settings.Theme = AppSettings.ThemeMode.Dark; ApplyAppearance(); };

        themePanel.Children.Add(themeFollowRadio);
        themePanel.Children.Add(themeLightRadio);
        themePanel.Children.Add(themeDarkRadio);
        panel.Children.Add(themePanel);

        // Opacity slider
        panel.Children.Add(new TextBlock { Text = $"不透明度: {_settings.WindowOpacity:P0}", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(BuildSliderRow("窗口透明度", _settings.WindowOpacity, 0.3, 1.0,
            v => { _settings.WindowOpacity = v; ApplyAppearance(); },
            step: 0.05));

        // Background blur type
        panel.Children.Add(new TextBlock { Text = "背景效果", FontWeight = FontWeights.SemiBold });
        var blurPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };

        var blurDefaultRadio = new RadioButton { Content = "默认", Tag = "default" };
        var blurMicaRadio = new RadioButton { Content = "云母 (Mica)", Tag = "mica" };
        var blurAcrylicRadio = new RadioButton { Content = "亚克力 (Acrylic)", Tag = "acrylic" };

        switch (_settings.BackgroundBlur)
        {
            case AppSettings.BlurMode.Mica: blurMicaRadio.IsChecked = true; break;
            case AppSettings.BlurMode.Acrylic: blurAcrylicRadio.IsChecked = true; break;
            default: blurDefaultRadio.IsChecked = true; break;
        }

        blurDefaultRadio.Checked += (_, _) => { _settings.BackgroundBlur = AppSettings.BlurMode.Default; ApplyAppearance(); };
        blurMicaRadio.Checked += (_, _) => { _settings.BackgroundBlur = AppSettings.BlurMode.Mica; ApplyAppearance(); };
        blurAcrylicRadio.Checked += (_, _) => { _settings.BackgroundBlur = AppSettings.BlurMode.Acrylic; ApplyAppearance(); };

        blurPanel.Children.Add(blurDefaultRadio);
        blurPanel.Children.Add(blurMicaRadio);
        blurPanel.Children.Add(blurAcrylicRadio);
        panel.Children.Add(blurPanel);

        // Background image
        panel.Children.Add(new TextBlock { Text = "背景图片", FontWeight = FontWeights.SemiBold });
        var bgPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var bgPathLabel = new TextBlock
        {
            Text = string.IsNullOrEmpty(_settings.BackgroundImagePath) ? "(无)" : Path.GetFileName(_settings.BackgroundImagePath),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 120,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var selectBgBtn = new Button { Content = "选择图片" };
        selectBgBtn.Click += (_, _) =>
        {
            var path = ShowImagePicker();
            if (!string.IsNullOrEmpty(path))
            {
                _settings.BackgroundImagePath = path;
                bgPathLabel.Text = Path.GetFileName(path);
                ApplyAppearance();
            }
        };

        var clearBgBtn = new Button { Content = "清除" };
        clearBgBtn.Click += (_, _) =>
        {
            _settings.BackgroundImagePath = "";
            bgPathLabel.Text = "(无)";
            ApplyAppearance();
        };

        bgPanel.Children.Add(bgPathLabel);
        bgPanel.Children.Add(selectBgBtn);
        bgPanel.Children.Add(clearBgBtn);
        panel.Children.Add(bgPanel);

        // Note about transparency support
        panel.Children.Add(new TextBlock
        {
            Text = "提示：透明度低于 100% 时窗口将启用分层透明",
            FontSize = 12, Opacity = 0.6
        });

        return panel;
    }

    private void ApplyAppearance()
    {
        AppearanceManager.ApplyAll(this, _settings);
        _settings.Save();
    }

    private string? ShowImagePicker()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            return ShowOpenFileDialog(hwnd, "选择背景图片",
                "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*");
        }
        catch (Exception ex)
        {
            AppLog.Log($"Image picker failed: {ex.Message}");
            return null;
        }
    }

    private static string? ShowOpenFileDialog(IntPtr hwnd, string title, string filter)
    {
        var ofn = new OPENFILENAME
        {
            lStructSize = Marshal.SizeOf<OPENFILENAME>(),
            hwndOwner = hwnd,
            lpstrTitle = title,
            lpstrFilter = filter.Replace('|', '\0') + "\0\0",
            nFilterIndex = 1,
            lpstrFile = new string('\0', 260),
            nMaxFile = 260,
            Flags = 0x00080000 | 0x00001000 // OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST
        };

        return GetOpenFileName(ref ofn) ? ofn.lpstrFile : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string? lpstrFilter;
        public string? lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public string lpstrFile;
        public int nMaxFile;
        public string? lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileName(ref OPENFILENAME ofn);

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontSize = 20,
        FontWeight = FontWeights.SemiBold
    };

    private FrameworkElement BuildCursorPage()
    {
        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(24, 16, 24, 16) };
        panel.Children.Add(Header("光标外观"));

        var sizeRow = new Grid();
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var sizeLabel = new TextBlock { Text = "光标大小", VerticalAlignment = VerticalAlignment.Center };
        var sizeBox = new TextBox { Text = _settings.CursorWidth.ToString("0.#"), Width = 80, VerticalAlignment = VerticalAlignment.Center };
        var sizeMinus = new Button { Content = "−", Width = 32, Height = 32, Margin = new Thickness(4, 0, 2, 0) };
        var sizePlus = new Button { Content = "+", Width = 32, Height = 32, Margin = new Thickness(2, 0, 0, 0) };

        void ApplySize()
        {
            if (double.TryParse(sizeBox.Text, out var v))
            {
                v = Math.Clamp(v, 16, 64);
                _settings.CursorWidth = v;
                sizeBox.Text = v.ToString("0.#");
                _settings.Save();
                _engine?.ApplyCursorWidth(v);
            }
            else
            {
            }
        }

        sizeBox.KeyDown += (_, e) => {
 if (e.Key == Windows.System.VirtualKey.Enter) { ApplySize(); e.Handled = true; } };
        sizeBox.LostFocus += (_, _) => ApplySize();
        sizeMinus.Click += (_, _) => { AppLog.Log("[UI] sizeMinus Click"); if (double.TryParse(sizeBox.Text, out var v)) { v = Math.Max(16, v - 1); sizeBox.Text = v.ToString("0.#"); ApplySize(); } };
        sizePlus.Click += (_, _) => { AppLog.Log("[UI] sizePlus Click"); if (double.TryParse(sizeBox.Text, out var v)) { v = Math.Min(64, v + 1); sizeBox.Text = v.ToString("0.#"); ApplySize(); } };

        var sizeButtons = new StackPanel { Orientation = Orientation.Horizontal };
        sizeButtons.Children.Add(sizeMinus);
        sizeButtons.Children.Add(sizePlus);

        Grid.SetColumn(sizeBox, 1);
        Grid.SetColumn(sizeButtons, 2);
        sizeRow.Children.Add(sizeLabel);
        sizeRow.Children.Add(sizeBox);
        sizeRow.Children.Add(sizeButtons);

        panel.Children.Add(sizeRow);
        return panel;
    }

    private FrameworkElement BuildAlignPage()
    {
        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(24, 16, 24, 16) };
        panel.Children.Add(Header("场景对齐"));

        // --- normal scene geometry ---
        panel.Children.Add(new TextBlock { Text = "主窗口场景", FontWeight = FontWeights.SemiBold, Opacity = 0.8 });

        var hotX = BuildSliderRow("热点 X", _settings.NormalHotspotX, -64, 64, v => { _settings.NormalHotspotX = v; _settings.Save(); }, () => _engine?.RefreshNormalSceneTuning());
        var hotY = BuildSliderRow("热点 Y", _settings.NormalHotspotY, -64, 64, v => { _settings.NormalHotspotY = v; _settings.Save(); }, () => _engine?.RefreshNormalSceneTuning());
        panel.Children.Add(hotX);
        panel.Children.Add(hotY);

        // --- dc scene geometry ---
        panel.Children.Add(new TextBlock { Text = "DC 场景（系统光标）", FontWeight = FontWeights.SemiBold, Opacity = 0.8, Margin = new Thickness(0, 12, 0, 0) });

        var dSize = BuildSliderRow("光标大小", _settings.DcCursorSize > 0 ? _settings.DcCursorSize : _settings.CursorWidth, 16, 64,
            v => { _settings.DcCursorSize = v; _settings.Save(); }, () => _engine?.ApplyDcSceneTuning());
        var dHotX = BuildSliderRow("热点 X", _settings.DcHotspotX, -64, 64, v => { _settings.DcHotspotX = v; _settings.Save(); }, () => _engine?.ApplyDcSceneTuning());
        var dHotY = BuildSliderRow("热点 Y", _settings.DcHotspotY, -64, 64, v => { _settings.DcHotspotY = v; _settings.Save(); }, () => _engine?.ApplyDcSceneTuning());
        panel.Children.Add(dSize);
        panel.Children.Add(dHotX);
        panel.Children.Add(dHotY);

        return panel;
    }

    private FrameworkElement BuildSoundPage()
    {
        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(24, 16, 24, 16) };
        panel.Children.Add(Header("音效"));

        var tapToggle = new ToggleSwitch
        {
            Header = "敲击音效",
            IsOn = _settings.TapSoundEnabled,
            OnContent = "开",
            OffContent = "关"
        };
        tapToggle.Toggled += (_, _) =>
        {
            _settings.TapSoundEnabled = tapToggle.IsOn;
            _settings.Save();
            _engine?.SetTapSoundEnabled(tapToggle.IsOn);
        };
        panel.Children.Add(tapToggle);

        var tapVolume = BuildSliderRow("音量", _settings.TapSoundVolume * 100, 0, 100, v => { _settings.TapSoundVolume = v / 100.0; _settings.Save(); }, step: 5);
        panel.Children.Add(tapVolume);

        var hoverToggle = new ToggleSwitch
        {
            Header = "悬停音效",
            IsOn = _settings.HoverSoundEnabled,
            OnContent = "开",
            OffContent = "关"
        };
        hoverToggle.Toggled += (_, _) =>
        {
            _settings.HoverSoundEnabled = hoverToggle.IsOn;
            _settings.Save();
            _engine?.SetHoverSoundEnabled(hoverToggle.IsOn);
        };
        panel.Children.Add(hoverToggle);

        var hoverVolume = BuildSliderRow("音量", _settings.HoverSoundVolume * 100, 0, 100, v => { _settings.HoverSoundVolume = v / 100.0; _settings.Save(); }, step: 5);
        panel.Children.Add(hoverVolume);

        return panel;
    }

    private FrameworkElement BuildSystemPage()
    {
        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(24, 16, 24, 16) };
        panel.Children.Add(Header("系统"));

        // Service management
        var isInstalled = ServiceManager.IsInstalled();
        var isRunning = isInstalled && ServiceManager.IsRunning();
        var autoStartEnabled = isInstalled && ServiceManager.IsAutoStartEnabled();

        var statusText = new TextBlock
        {
            Text = isInstalled
                ? (isRunning ? "服务状态：运行中" : "服务状态：已停止")
                : "服务状态：未安装",
            Foreground = isRunning ? new SolidColorBrush(Colors.Green) : new SolidColorBrush(Colors.Gray),
            FontWeight = FontWeights.SemiBold
        };
        panel.Children.Add(statusText);

        // Start/Stop service button
        var toggleServiceBtn = new Button
        {
            Content = isRunning ? "停止服务" : "启动服务",
            MinWidth = 120
        };
        toggleServiceBtn.Click += (_, _) =>
        {
            if (ServiceManager.IsRunning())
            {
                ServiceManager.Stop();
            }
            else
            {
                ServiceManager.Start();
            }
            // Refresh panel
            _settings.Save();
        };
        panel.Children.Add(toggleServiceBtn);

        // Auto-start checkbox
        var autoStartCheck = new CheckBox
        {
            Content = "开机自启服务",
            IsChecked = autoStartEnabled,
            IsEnabled = isInstalled
        };
        autoStartCheck.Checked += (_, _) =>
        {
            if (ServiceManager.SetAutoStart(true))
            {
                _settings.AutoStart = true;
                _settings.Save();
            }
        };
        autoStartCheck.Unchecked += (_, _) =>
        {
            if (ServiceManager.SetAutoStart(false))
            {
                _settings.AutoStart = false;
                _settings.Save();
            }
        };
        panel.Children.Add(autoStartCheck);

        // Install/Uninstall service buttons (require admin)
        var installPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var installBtn = new Button
        {
            Content = "安装服务",
            MinWidth = 100,
            IsEnabled = !isInstalled
        };
        installBtn.Click += (_, _) =>
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                ServiceManager.Install(exePath);
            }
        };
        installPanel.Children.Add(installBtn);

        var uninstallBtn = new Button
        {
            Content = "卸载服务",
            MinWidth = 100,
            IsEnabled = isInstalled
        };
        uninstallBtn.Click += (_, _) =>
        {
            ServiceManager.Uninstall();
        };
        installPanel.Children.Add(uninstallBtn);

        panel.Children.Add(installPanel);
        panel.Children.Add(new TextBlock
        {
            Text = "安装服务需要管理员权限",
            FontSize = 12,
            Opacity = 0.6
        });

        return panel;
    }

    /// <summary>
    /// A label + TextBox (with +/- buttons) row for numeric settings.
    /// Used instead of WinUI 3 Slider which has unreliable ValueChanged events.
    /// </summary>
    private FrameworkElement BuildSliderRow(string label, double value, double min, double max, Action<double> setter, Action? after = null, double step = 1.0)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelText = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, MinWidth = 90 };
        var valueBox = new TextBox { Text = value.ToString("0.##"), Width = 80, VerticalAlignment = VerticalAlignment.Center };
        var minusBtn = new Button { Content = "−", Width = 32, Height = 32, Margin = new Thickness(4, 0, 2, 0) };
        var plusBtn = new Button { Content = "+", Width = 32, Height = 32, Margin = new Thickness(2, 0, 0, 0) };

        void ApplyValue()
        {
            if (double.TryParse(valueBox.Text, out var v))
            {
                v = Math.Clamp(v, min, max);
                setter(v);
                _settings.Save();
                after?.Invoke();
            }
        }

        valueBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                ApplyValue();
                e.Handled = true;
            }
        };
        valueBox.LostFocus += (_, _) => {
 ApplyValue(); };

        minusBtn.Click += (_, _) =>
        {
            if (double.TryParse(valueBox.Text, out var v))
            {
                v = Math.Max(min, v - step);
                valueBox.Text = v.ToString("0.##");
                ApplyValue();
            }
        };
        plusBtn.Click += (_, _) =>
        {
            if (double.TryParse(valueBox.Text, out var v))
            {
                v = Math.Min(max, v + step);
                valueBox.Text = v.ToString("0.##");
                ApplyValue();
            }
        };

        var buttonsPanel = new StackPanel { Orientation = Orientation.Horizontal };
        buttonsPanel.Children.Add(minusBtn);
        buttonsPanel.Children.Add(plusBtn);

        Grid.SetColumn(valueBox, 1);
        Grid.SetColumn(buttonsPanel, 2);
        row.Children.Add(labelText);
        row.Children.Add(valueBox);
        row.Children.Add(buttonsPanel);
        return row;
    }
}
