using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace OsuCursorWin;

internal sealed class SettingsWindow : Window
{
    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(18, 19, 24));
    private static readonly Brush PanelBrush = new SolidColorBrush(Color.FromRgb(30, 31, 38));
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(255, 102, 171));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(238, 239, 244));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(158, 160, 172));

    private readonly AppSettings _settings;
    private readonly Slider _sizeSlider;
    private readonly TextBlock _sizeValueText;
    private readonly CheckBox _autoStartCheckBox;
    private readonly CheckBox _tapSoundCheckBox;
    private readonly CheckBox _hoverSoundCheckBox;
    private readonly CheckBox _resizeSoundCheckBox;
    private readonly Slider _tapSoundVolumeSlider;
    private readonly TextBlock _tapSoundVolumeText;
    private readonly Slider _hoverSoundVolumeSlider;
    private readonly TextBlock _hoverSoundVolumeText;
    private bool _allowClose;
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

    internal SettingsWindow(AppSettings settings)
    {
        _settings = settings;

        Title = "osu! Cursor 设置";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanMinimize;
        ShowInTaskbar = true;
        Icon = ProgramIcon.CreateWindowIcon();
        Background = BackgroundBrush;
        Foreground = TextBrush;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        UseLayoutRounding = true;

        var root = new StackPanel
        {
            Margin = new Thickness(20)
        };

        root.Children.Add(new TextBlock
        {
            Text = "设置",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = AccentBrush,
            Margin = new Thickness(0, 0, 0, 18)
        });

        var cursorSection = new StackPanel();
        _sizeValueText = new TextBlock
        {
            Text = $"{settings.CursorWidth:0} px",
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = TextBrush,
            VerticalAlignment = VerticalAlignment.Center
        };

        var cursorValuePanel = new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(_sizeValueText, Dock.Right);
        cursorValuePanel.Children.Add(_sizeValueText);
        cursorValuePanel.Children.Add(new TextBlock
        {
            Text = "16 - 64",
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center
        });

        _sizeSlider = new Slider
        {
            Minimum = 16,
            Maximum = 64,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Value = settings.CursorWidth
        };
        StyleSlider(_sizeSlider);
        _sizeSlider.ValueChanged += OnSizeSliderChanged;

        var resetButton = new Button
        {
            Content = "恢复默认",
            Width = 96,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 12, 0, 0),
            Background = AccentBrush,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 6, 12, 6)
        };
        resetButton.Click += (_, _) => _sizeSlider.Value = 30;

        cursorSection.Children.Add(cursorValuePanel);
        cursorSection.Children.Add(_sizeSlider);
        cursorSection.Children.Add(resetButton);
        root.Children.Add(CreateSection("光标", cursorSection));

        var soundSection = new StackPanel();

        _tapSoundCheckBox = new CheckBox
        {
            Content = "敲击音效",
            IsChecked = settings.TapSoundEnabled,
            Foreground = TextBrush,
            FontSize = 13,
            Margin = new Thickness(0, 2, 0, 4)
        };
        _tapSoundCheckBox.Checked += OnTapSoundChecked;
        _tapSoundCheckBox.Unchecked += OnTapSoundChecked;

        _tapSoundVolumeText = new TextBlock
        {
            Text = $"{Math.Round(settings.TapSoundVolume * 100):0}%",
            Foreground = TextBrush,
            VerticalAlignment = VerticalAlignment.Center
        };

        _tapSoundVolumeSlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Value = Math.Clamp(settings.TapSoundVolume * 100, 0, 100),
            VerticalAlignment = VerticalAlignment.Center
        };
        StyleSlider(_tapSoundVolumeSlider);
        _tapSoundVolumeSlider.ValueChanged += OnTapSoundVolumeChanged;

        _hoverSoundCheckBox = new CheckBox
        {
            Content = "悬停音效",
            IsChecked = settings.HoverSoundEnabled,
            Foreground = TextBrush,
            FontSize = 13,
            Margin = new Thickness(0, 10, 0, 4)
        };
        _hoverSoundCheckBox.Checked += OnHoverSoundChecked;
        _hoverSoundCheckBox.Unchecked += OnHoverSoundChecked;

        _hoverSoundVolumeText = new TextBlock
        {
            Text = $"{Math.Round(settings.HoverSoundVolume * 100):0}%",
            Foreground = TextBrush,
            VerticalAlignment = VerticalAlignment.Center
        };

        _hoverSoundVolumeSlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Value = Math.Clamp(settings.HoverSoundVolume * 100, 0, 100),
            VerticalAlignment = VerticalAlignment.Center
        };
        StyleSlider(_hoverSoundVolumeSlider);
        _hoverSoundVolumeSlider.ValueChanged += OnHoverSoundVolumeChanged;

        _resizeSoundCheckBox = new CheckBox
        {
            Content = "窗口拉伸时播放",
            IsChecked = settings.HoverSoundAsResizePrompt,
            Foreground = TextBrush,
            FontSize = 13,
            Margin = new Thickness(0, 10, 0, 0)
        };
        _resizeSoundCheckBox.Checked += OnResizeSoundModeChecked;
        _resizeSoundCheckBox.Unchecked += OnResizeSoundModeChecked;

        soundSection.Children.Add(_tapSoundCheckBox);
        soundSection.Children.Add(CreateSliderRow("音量", _tapSoundVolumeSlider, _tapSoundVolumeText));
        soundSection.Children.Add(_hoverSoundCheckBox);
        soundSection.Children.Add(CreateSliderRow("悬停音量", _hoverSoundVolumeSlider, _hoverSoundVolumeText));
        soundSection.Children.Add(_resizeSoundCheckBox);
        root.Children.Add(CreateSection("音效", soundSection));

        var systemSection = new StackPanel();
        _autoStartCheckBox = new CheckBox
        {
            Content = "开机自启",
            IsChecked = settings.AutoStart,
            Foreground = TextBrush,
            FontSize = 13
        };
        _autoStartCheckBox.Checked += OnAutoStartChecked;
        _autoStartCheckBox.Unchecked += OnAutoStartChecked;
        systemSection.Children.Add(_autoStartCheckBox);
        root.Children.Add(CreateSection("系统", systemSection));

        Content = root;

        StateChanged += OnStateChanged;
        Closing += OnClosing;
    }

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

    private void OnSizeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var width = Math.Round(e.NewValue);
        _settings.CursorWidth = width;
        _sizeValueText.Text = $"{width:0} px";
        _settings.Save();
        CursorSizeChanged?.Invoke(width);
    }

    private void OnAutoStartChecked(object? sender, RoutedEventArgs e)
    {
        if (_autoStartUpdating)
        {
            return;
        }

        AutoStartChanged?.Invoke(_autoStartCheckBox.IsChecked == true);
    }

    private void OnTapSoundChecked(object? sender, RoutedEventArgs e)
    {
        if (_tapSoundUpdating)
        {
            return;
        }

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
        if (_hoverSoundUpdating)
        {
            return;
        }

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
        if (_resizeSoundUpdating)
        {
            return;
        }

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

    private static Border CreateSection(string title, params UIElement[] children)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = AccentBrush,
            Margin = new Thickness(0, 0, 0, 10)
        });

        foreach (var child in children)
        {
            stack.Children.Add(child);
        }

        return new Border
        {
            Background = PanelBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 12),
            Child = stack
        };
    }

    private static DockPanel CreateSliderRow(string label, Slider slider, TextBlock valueText)
    {
        var panel = new DockPanel
        {
            Margin = new Thickness(0, 6, 0, 0)
        };

        valueText.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(valueText, Dock.Right);

        var labelText = new TextBlock
        {
            Text = label,
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
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
}
