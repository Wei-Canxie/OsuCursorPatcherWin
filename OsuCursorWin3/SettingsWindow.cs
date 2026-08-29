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

internal sealed class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly CursorEngine? _engine;
    private TextBlock? _titleBarText;
    private Border? _titleBarRoot;

    public SettingsWindow(CursorEngine? engine = null)
    {
        _engine = engine;
        _settings = engine?.GetSettings() ?? AppSettings.Load();
        Title = "osu! Cursor 设置";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(960, 680));

        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            AppWindow.Hide();
        };

        ExtendsContentIntoTitleBar = true;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _titleBarRoot = new Border { Height = 32, Background = GetTitleBarBrush() };
        _titleBarText = new TextBlock
        {
            Text = Title,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.Black)
        };
        _titleBarRoot.Child = _titleBarText;
        Grid.SetRow(_titleBarRoot, 0);

        var nav = new NavigationView
        {
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            OpenPaneLength = 200,
        };

        nav.MenuItems.Add(new NavigationViewItem { Content = "外观", Icon = new SymbolIcon(Symbol.View), Tag = "appearance" });
        nav.MenuItems.Add(new NavigationViewItem { Content = "光标", Icon = new SymbolIcon(Symbol.Target), Tag = "cursor" });
        nav.MenuItems.Add(new NavigationViewItem { Content = "场景对齐", Icon = new SymbolIcon(Symbol.AlignCenter), Tag = "align" });
        nav.MenuItems.Add(new NavigationViewItem { Content = "音效", Icon = new SymbolIcon(Symbol.Audio), Tag = "sound" });
        nav.MenuItems.Add(new NavigationViewItem { Content = "系统", Icon = new SymbolIcon(Symbol.Setting), Tag = "system" });

        nav.SelectionChanged += (s, e) =>
        {
            if (nav.SelectedItem is NavigationViewItem item && item.Tag is string tag)
                nav.Content = BuildPage(tag);
        };

        nav.Loaded += (_, _) =>
        {
            try { nav.SelectedItem = nav.MenuItems[0]; }
            catch (Exception ex) { AppLog.Log($"nav.Loaded set SelectedItem failed: {ex.Message}"); }
            ApplyAppearance();
        };

        Grid.SetRow(nav, 1);

        root.Children.Add(_titleBarRoot);
        root.Children.Add(nav);
        Content = root;
    }

    private Brush GetTitleBarBrush()
    {
        var titleBarOpacity = _settings.WindowOpacity <= 0.9
            ? Math.Clamp(_settings.WindowOpacity + 0.1, 0, 1)
            : _settings.WindowOpacity;

        var isDark = _settings.Theme == AppSettings.ThemeMode.Dark ||
                     (_settings.Theme == AppSettings.ThemeMode.FollowSystem && IsSystemDark());

        var color = isDark
            ? Color.FromArgb((byte)(titleBarOpacity * 255), 0x2D, 0x2D, 0x2D)
            : Color.FromArgb((byte)(titleBarOpacity * 255), 0xF3, 0xF3, 0xF3);

        return new SolidColorBrush(color);
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
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(24, 16, 24, 16) };
        panel.Children.Add(Header("外观设置"));

        // Theme selection
        panel.Children.Add(new TextBlock { Text = "主题", FontWeight = FontWeights.SemiBold });
        var themePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };

        var themeFollowRadio = new RadioButton { Content = "跟随系统" };
        var themeLightRadio = new RadioButton { Content = "亮色" };
        var themeDarkRadio = new RadioButton { Content = "暗色" };

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

        // Window opacity: Slider + TextBox + buttons
        var opacityLabel = new TextBlock { Text = $"窗口不透明度: {_settings.WindowOpacity:P0}", FontWeight = FontWeights.SemiBold };
        panel.Children.Add(opacityLabel);
        panel.Children.Add(BuildSliderWithTextBox("窗口不透明度", _settings.WindowOpacity, 0.3, 1.0,
            v => { _settings.WindowOpacity = v; opacityLabel.Text = $"窗口不透明度: {v:P0}"; ApplyAppearance(); },
            step: 0.05, format: "0%"));

        // Background blur type
        panel.Children.Add(new TextBlock { Text = "背景效果", FontWeight = FontWeights.SemiBold });
        var blurPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };

        var blurDefaultRadio = new RadioButton { Content = "默认" };
        var blurMicaRadio = new RadioButton { Content = "云母 (Mica)" };
        var blurAcrylicRadio = new RadioButton { Content = "亚克力 (Acrylic)" };

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

        // Blur radius slider (pixel radius, slider 0-255, text can go up to 1024)
        var blurRadiusLabel = new TextBlock { Text = $"模糊半径: {_settings.BackgroundBlurRadius}px", FontWeight = FontWeights.SemiBold };
        panel.Children.Add(blurRadiusLabel);
        panel.Children.Add(BuildSliderWithTextBox("模糊半径", _settings.BackgroundBlurRadius, 0, 255,
            v => { _settings.BackgroundBlurRadius = (int)v; blurRadiusLabel.Text = $"模糊半径: {(int)v}px"; ApplyAppearance(); },
            step: 1, format: "0", textMin: 0, textMax: 1024));

        if (!IsBlurSupported())
        {
            panel.Children.Add(new TextBlock { Text = "当前系统不支持 Mica/Acrylic", FontSize = 12, Opacity = 0.6 });
        }

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

        // Background image opacity: Slider + TextBox + buttons
        var bgOpacityLabel = new TextBlock { Text = $"背景图片不透明度: {_settings.BackgroundImageOpacity:P0}", FontWeight = FontWeights.SemiBold };
        panel.Children.Add(bgOpacityLabel);
        panel.Children.Add(BuildSliderWithTextBox("背景图片不透明度", _settings.BackgroundImageOpacity, 0.0, 1.0,
            v => { _settings.BackgroundImageOpacity = v; bgOpacityLabel.Text = $"背景图片不透明度: {v:P0}"; ApplyAppearance(); },
            step: 0.05, format: "0%"));

        return panel;
    }

    private void ApplyAppearance()
    {
        AppearanceManager.ApplyAll(this, _settings);

        if (_titleBarRoot != null)
        {
            _titleBarRoot.Background = GetTitleBarBrush();
        }

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
            Flags = 0x00080000 | 0x00001000
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

    /// <summary>
    /// Build a row with: label | Slider | TextBox | + / - buttons.
    /// The Slider provides quick drag adjustment within sliderMin/sliderMax.
    /// TextBox allows precise input within textMin/textMax (can exceed slider range).
    /// </summary>
    private FrameworkElement BuildSliderWithTextBox(string label, double value, double sliderMin, double sliderMax, Action<double> apply, double step = 1.0, string format = "0.##", double? textMin = null, double? textMax = null)
    {
        double tMin = textMin ?? sliderMin;
        double tMax = textMax ?? sliderMax;

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelText = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider
        {
            Minimum = sliderMin,
            Maximum = sliderMax,
            Value = Math.Clamp(value, sliderMin, sliderMax),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            SmallChange = step,
            LargeChange = step * 10,
            StepFrequency = step
        };
        var valueBox = new TextBox { Text = value.ToString(format), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        var minusBtn = new Button
        {
            Content = "−",
            Width = 32,
            Height = 32,
            FontSize = 16,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 1, 0)
        };
        var plusBtn = new Button
        {
            Content = "+",
            Width = 32,
            Height = 32,
            FontSize = 16,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1, 0, 2, 0)
        };

        // Slider value changed -> update textbox and apply
        slider.ValueChanged += (_, _) =>
        {
            var v = Math.Clamp(slider.Value, sliderMin, sliderMax);
            valueBox.Text = v.ToString(format);
            apply(v);
        };

        // TextBox input (can exceed slider range)
        void ApplyFromText()
        {
            if (double.TryParse(valueBox.Text, out var v))
            {
                v = Math.Clamp(v, tMin, tMax);
                // Only update slider if value is within slider range
                if (v >= sliderMin && v <= sliderMax)
                    slider.Value = v;
                valueBox.Text = v.ToString(format);
                apply(v);
            }
            else
            {
                valueBox.Text = slider.Value.ToString(format);
            }
        }

        valueBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) { ApplyFromText(); e.Handled = true; }
        };
        valueBox.LostFocus += (_, _) => ApplyFromText();

        // +/- buttons
        minusBtn.Click += (_, _) =>
        {
            var v = Math.Max(sliderMin, slider.Value - step);
            slider.Value = v;
        };
        plusBtn.Click += (_, _) =>
        {
            var v = Math.Min(sliderMax, slider.Value + step);
            slider.Value = v;
        };

        var buttonsPanel = new StackPanel { Orientation = Orientation.Horizontal };
        buttonsPanel.Children.Add(minusBtn);
        buttonsPanel.Children.Add(plusBtn);

        Grid.SetColumn(labelText, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(valueBox, 2);
        Grid.SetColumn(buttonsPanel, 3);
        grid.Children.Add(labelText);
        grid.Children.Add(slider);
        grid.Children.Add(valueBox);
        grid.Children.Add(buttonsPanel);

        return grid;
    }

    private FrameworkElement BuildCursorPage()
    {
        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(24, 16, 24, 16) };
        panel.Children.Add(Header("光标外观"));

        panel.Children.Add(BuildSliderWithTextBox("光标大小", _settings.CursorWidth, 16, 64,
            v => { _settings.CursorWidth = v; _settings.Save(); _engine?.ApplyCursorWidth(v); },
            step: 1, format: "0.#"));

        return panel;
    }

    private FrameworkElement BuildAlignPage()
    {
        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(24, 16, 24, 16) };
        panel.Children.Add(Header("场景对齐"));

        panel.Children.Add(new TextBlock { Text = "主窗口场景", FontWeight = FontWeights.SemiBold, Opacity = 0.8 });
        panel.Children.Add(BuildSliderWithTextBox("热点 X", _settings.NormalHotspotX, -64, 64,
            v => { _settings.NormalHotspotX = v; _settings.Save(); }, step: 0.5));
        panel.Children.Add(BuildSliderWithTextBox("热点 Y", _settings.NormalHotspotY, -64, 64,
            v => { _settings.NormalHotspotY = v; _settings.Save(); }, step: 0.5));

        panel.Children.Add(new TextBlock { Text = "DC 场景（系统光标）", FontWeight = FontWeights.SemiBold, Opacity = 0.8, Margin = new Thickness(0, 12, 0, 0) });

        panel.Children.Add(BuildSliderWithTextBox("光标大小", _settings.DcCursorSize > 0 ? _settings.DcCursorSize : _settings.CursorWidth, 16, 64,
            v => { _settings.DcCursorSize = v; _settings.Save(); _engine?.ApplyDcSceneTuning(); }, step: 1));
        panel.Children.Add(BuildSliderWithTextBox("热点 X", _settings.DcHotspotX, -64, 64,
            v => { _settings.DcHotspotX = v; _settings.Save(); _engine?.ApplyDcSceneTuning(); }, step: 0.5));
        panel.Children.Add(BuildSliderWithTextBox("热点 Y", _settings.DcHotspotY, -64, 64,
            v => { _settings.DcHotspotY = v; _settings.Save(); _engine?.ApplyDcSceneTuning(); }, step: 0.5));

        return panel;
    }

    private FrameworkElement BuildSoundPage()
    {
        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(24, 16, 24, 16) };
        panel.Children.Add(Header("音效"));

        var tapToggle = new ToggleSwitch { Header = "敲击音效", IsOn = _settings.TapSoundEnabled, OnContent = "开", OffContent = "关" };
        tapToggle.Toggled += (_, _) => { _settings.TapSoundEnabled = tapToggle.IsOn; _settings.Save(); _engine?.SetTapSoundEnabled(tapToggle.IsOn); };
        panel.Children.Add(tapToggle);

        panel.Children.Add(BuildSliderWithTextBox("敲击音量", _settings.TapSoundVolume * 100, 0, 100,
            v => { _settings.TapSoundVolume = v / 100.0; _settings.Save(); }, step: 5, format: "0"));

        var hoverToggle = new ToggleSwitch { Header = "悬停音效", IsOn = _settings.HoverSoundEnabled, OnContent = "开", OffContent = "关" };
        hoverToggle.Toggled += (_, _) => { _settings.HoverSoundEnabled = hoverToggle.IsOn; _settings.Save(); _engine?.SetHoverSoundEnabled(hoverToggle.IsOn); };
        panel.Children.Add(hoverToggle);

        panel.Children.Add(BuildSliderWithTextBox("悬停音量", _settings.HoverSoundVolume * 100, 0, 100,
            v => { _settings.HoverSoundVolume = v / 100.0; _settings.Save(); }, step: 5, format: "0"));

        return panel;
    }

    private FrameworkElement BuildSystemPage()
    {
        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(24, 16, 24, 16) };
        panel.Children.Add(Header("系统"));

        var isInstalled = ServiceManager.IsInstalled();
        var isRunning = isInstalled && ServiceManager.IsRunning();
        var autoStartEnabled = isInstalled && ServiceManager.IsAutoStartEnabled();

        var statusText = new TextBlock
        {
            Text = isInstalled ? (isRunning ? "服务状态：运行中" : "服务状态：已停止") : "服务状态：未安装",
            Foreground = isRunning ? new SolidColorBrush(Colors.Green) : new SolidColorBrush(Colors.Gray),
            FontWeight = FontWeights.SemiBold
        };
        panel.Children.Add(statusText);

        var toggleServiceBtn = new Button { Content = isRunning ? "停止服务" : "启动服务", MinWidth = 120 };
        toggleServiceBtn.Click += (_, _) =>
        {
            if (ServiceManager.IsRunning()) ServiceManager.Stop(); else ServiceManager.Start();
            _settings.Save();
        };
        panel.Children.Add(toggleServiceBtn);

        var autoStartCheck = new CheckBox { Content = "开机自启服务", IsChecked = autoStartEnabled, IsEnabled = isInstalled };
        autoStartCheck.Checked += (_, _) => { if (ServiceManager.SetAutoStart(true)) { _settings.AutoStart = true; _settings.Save(); } };
        autoStartCheck.Unchecked += (_, _) => { if (ServiceManager.SetAutoStart(false)) { _settings.AutoStart = false; _settings.Save(); } };
        panel.Children.Add(autoStartCheck);

        var installPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var installBtn = new Button { Content = "安装服务", MinWidth = 100, IsEnabled = !isInstalled };
        installBtn.Click += (_, _) => { var exePath = Environment.ProcessPath; if (!string.IsNullOrEmpty(exePath)) ServiceManager.Install(exePath); };
        installPanel.Children.Add(installBtn);

        var uninstallBtn = new Button { Content = "卸载服务", MinWidth = 100, IsEnabled = isInstalled };
        uninstallBtn.Click += (_, _) => ServiceManager.Uninstall();
        installPanel.Children.Add(uninstallBtn);

        panel.Children.Add(installPanel);
        panel.Children.Add(new TextBlock { Text = "安装服务需要管理员权限", FontSize = 12, Opacity = 0.6 });

        return panel;
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int i && i == 0;
        }
        catch { return false; }
    }

    private static bool IsBlurSupported()
    {
        try
        {
            var os = Environment.OSVersion;
            return os.Version.Build >= 17763;
        }
        catch { return false; }
    }
}
