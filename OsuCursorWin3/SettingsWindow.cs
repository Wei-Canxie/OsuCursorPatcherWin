using System;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

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

        var aspectX = BuildSliderRow("横向缩放", _settings.NormalAspectX, 0.5, 2.0, v => { _settings.NormalAspectX = v; _settings.Save(); }, () => _engine?.RefreshNormalSceneTuning());
        var aspectY = BuildSliderRow("纵向缩放", _settings.NormalAspectY, 0.5, 2.0, v => { _settings.NormalAspectY = v; _settings.Save(); }, () => _engine?.RefreshNormalSceneTuning());
        var hotX = BuildSliderRow("热点 X", _settings.NormalHotspotX, -64, 64, v => { _settings.NormalHotspotX = v; _settings.Save(); }, () => _engine?.RefreshNormalSceneTuning());
        var hotY = BuildSliderRow("热点 Y", _settings.NormalHotspotY, -64, 64, v => { _settings.NormalHotspotY = v; _settings.Save(); }, () => _engine?.RefreshNormalSceneTuning());
        panel.Children.Add(aspectX);
        panel.Children.Add(aspectY);
        panel.Children.Add(hotX);
        panel.Children.Add(hotY);

        // --- dc scene geometry ---
        panel.Children.Add(new TextBlock { Text = "DC 场景（系统光标）", FontWeight = FontWeights.SemiBold, Opacity = 0.8, Margin = new Thickness(0, 12, 0, 0) });

        var dSize = BuildSliderRow("光标大小", _settings.DcCursorSize > 0 ? _settings.DcCursorSize : _settings.CursorWidth, 16, 64,
            v => { _settings.DcCursorSize = v; _settings.Save(); }, () => _engine?.ApplyDcSceneTuning());
        var dAspectX = BuildSliderRow("横向缩放", _settings.DcAspectX, 0.5, 2.0, v => { _settings.DcAspectX = v; _settings.Save(); }, () => _engine?.ApplyDcSceneTuning());
        var dAspectY = BuildSliderRow("纵向缩放", _settings.DcAspectY, 0.5, 2.0, v => { _settings.DcAspectY = v; _settings.Save(); }, () => _engine?.ApplyDcSceneTuning());
        var dHotX = BuildSliderRow("热点 X", _settings.DcHotspotX, -64, 64, v => { _settings.DcHotspotX = v; _settings.Save(); }, () => _engine?.ApplyDcSceneTuning());
        var dHotY = BuildSliderRow("热点 Y", _settings.DcHotspotY, -64, 64, v => { _settings.DcHotspotY = v; _settings.Save(); }, () => _engine?.ApplyDcSceneTuning());
        panel.Children.Add(dSize);
        panel.Children.Add(dAspectX);
        panel.Children.Add(dAspectY);
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
        tapToggle.Toggled += (_, _) => { _settings.TapSoundEnabled = tapToggle.IsOn; _settings.Save(); };
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
        hoverToggle.Toggled += (_, _) => { _settings.HoverSoundEnabled = hoverToggle.IsOn; _settings.Save(); };
        panel.Children.Add(hoverToggle);

        var hoverVolume = BuildSliderRow("音量", _settings.HoverSoundVolume * 100, 0, 100, v => { _settings.HoverSoundVolume = v / 100.0; _settings.Save(); }, step: 5);
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
