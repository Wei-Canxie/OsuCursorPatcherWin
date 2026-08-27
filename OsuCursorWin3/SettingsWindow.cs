using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace OsuCursorWin;

/// <summary>
/// WinUI 3 settings window built in code with real Fluent/WinUI controls:
/// NavigationView, Slider, ToggleSwitch, ComboBox, CheckBox.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly CursorEngine? _engine;
    public SettingsWindow(CursorEngine? engine = null)
    {
        _engine = engine;
        Title = "osu! Cursor 设置";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(960, 680));

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
        };

        root.Children.Add(nav);
        Content = root;
    }

    private object BuildPage(string tag)
    {
        return tag switch
        {
            "cursor" => BuildCursorPage(),
            "align" => BuildAlignPage(),
            "sound" => BuildSoundPage(),
            "system" => BuildSystemPage(),
            _ => new TextBlock { Text = tag }
        };
    }

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
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var sizeLabel = new TextBlock { Text = "光标大小", VerticalAlignment = VerticalAlignment.Center };
        var sizeSlider = new Slider
        {
            Minimum = 16, Maximum = 64, Value = _settings.CursorWidth,
            Width = 360, VerticalAlignment = VerticalAlignment.Center
        };
        var sizeValue = new TextBlock
        {
            Text = _settings.CursorWidth.ToString("0.#"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 48,
            TextAlignment = TextAlignment.Right
        };
        sizeSlider.ValueChanged += (_, e) =>
        {
            _settings.CursorWidth = e.NewValue;
            sizeValue.Text = e.NewValue.ToString("0.#");
            _engine?.ApplyCursorWidth(e.NewValue);
            _settings.Save();
        };

        Grid.SetColumn(sizeSlider, 1);
        Grid.SetColumn(sizeValue, 2);
        sizeRow.Children.Add(sizeLabel);
        sizeRow.Children.Add(sizeSlider);
        sizeRow.Children.Add(sizeValue);

        panel.Children.Add(sizeRow);
        return panel;
    }

    private FrameworkElement BuildAlignPage()
    {
        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(24, 16, 24, 16) };
        panel.Children.Add(Header("场景对齐"));

        // --- normal scene geometry ---
        panel.Children.Add(new TextBlock { Text = "主窗口场景", FontWeight = FontWeights.SemiBold, Opacity = 0.8 });

        var aspectX = BuildSliderRow("横向缩放", _settings.NormalAspectX, 0.5, 2.0, v => _settings.NormalAspectX = v);
        var aspectY = BuildSliderRow("纵向缩放", _settings.NormalAspectY, 0.5, 2.0, v => _settings.NormalAspectY = v);
        var hotX = BuildSliderRow("热点 X", _settings.NormalHotspotX, -64, 64, v => _settings.NormalHotspotX = v);
        var hotY = BuildSliderRow("热点 Y", _settings.NormalHotspotY, -64, 64, v => _settings.NormalHotspotY = v);
        panel.Children.Add(aspectX);
        panel.Children.Add(aspectY);
        panel.Children.Add(hotX);
        panel.Children.Add(hotY);

        // --- dc scene geometry ---
        panel.Children.Add(new TextBlock { Text = "DC 场景（系统光标）", FontWeight = FontWeights.SemiBold, Opacity = 0.8, Margin = new Thickness(0, 12, 0, 0) });

        var dSize = BuildSliderRow("光标大小", _settings.DcCursorSize > 0 ? _settings.DcCursorSize : _settings.CursorWidth, 16, 64,
            v => _settings.DcCursorSize = v, () => _engine?.ApplyDcSceneTuning());
        var dAspectX = BuildSliderRow("横向缩放", _settings.DcAspectX, 0.5, 2.0, v => _settings.DcAspectX = v, () => _engine?.ApplyDcSceneTuning());
        var dAspectY = BuildSliderRow("纵向缩放", _settings.DcAspectY, 0.5, 2.0, v => _settings.DcAspectY = v, () => _engine?.ApplyDcSceneTuning());
        var dHotX = BuildSliderRow("热点 X", _settings.DcHotspotX, -64, 64, v => _settings.DcHotspotX = v, () => _engine?.ApplyDcSceneTuning());
        var dHotY = BuildSliderRow("热点 Y", _settings.DcHotspotY, -64, 64, v => _settings.DcHotspotY = v, () => _engine?.ApplyDcSceneTuning());
        panel.Children.Add(dSize);
        panel.Children.Add(dAspectX);
        panel.Children.Add(dAspectY);
        panel.Children.Add(dHotX);
        panel.Children.Add(dHotY);

        return panel;
    }

    private FrameworkElement BuildSliderRow(string label, double value, double min, double max, Action<double> setter, Action? after = null)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelText = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, MinWidth = 90 };
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = value,
            Width = 360, VerticalAlignment = VerticalAlignment.Center
        };
        var valueText = new TextBlock
        {
            Text = value.ToString("0.##"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 48,
            TextAlignment = TextAlignment.Right
        };
        slider.ValueChanged += (_, e) =>
        {
            setter(e.NewValue);
            valueText.Text = e.NewValue.ToString("0.##");
            after?.Invoke();
            _settings.Save();
        };

        Grid.SetColumn(slider, 1);
        Grid.SetColumn(valueText, 2);
        row.Children.Add(labelText);
        row.Children.Add(slider);
        row.Children.Add(valueText);
        return row;
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
        tapToggle.Toggled += (_, _) => { _settings.TapSoundEnabled = tapToggle.IsOn; _settings.Save(); };
        panel.Children.Add(tapToggle);

        var tapVolume = BuildSliderRow("音量", _settings.TapSoundVolume * 100, 0, 100,
            v => _settings.TapSoundVolume = v / 100.0);
        panel.Children.Add(tapVolume);

        var hoverToggle = new ToggleSwitch
        {
            Header = "悬停音效",
            IsOn = _settings.HoverSoundEnabled,
            OnContent = "开",
            OffContent = "关"
        };
        hoverToggle.Toggled += (_, _) => { _settings.HoverSoundEnabled = hoverToggle.IsOn; _settings.Save(); };
        panel.Children.Add(hoverToggle);

        var hoverVolume = BuildSliderRow("音量", _settings.HoverSoundVolume * 100, 0, 100,
            v => _settings.HoverSoundVolume = v / 100.0);
        panel.Children.Add(hoverVolume);

        return panel;
    }

    private FrameworkElement BuildSystemPage()
    {
        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(24, 16, 24, 16) };
        panel.Children.Add(Header("系统"));

        var autoStart = new ToggleSwitch
        {
            Header = "开机自启",
            IsOn = _settings.AutoStart,
            OnContent = "开",
            OffContent = "关"
        };
        autoStart.Toggled += (_, _) =>
        {
            _settings.AutoStart = autoStart.IsOn;
            AutoStartManager.Apply(autoStart.IsOn);
            _settings.Save();
        };
        panel.Children.Add(autoStart);

        return panel;
    }
}