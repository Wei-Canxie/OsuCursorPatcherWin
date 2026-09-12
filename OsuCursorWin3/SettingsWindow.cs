using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using WinRT.Interop;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OsuCursorWin;

/// <summary>
/// A page container that tracks event subscriptions and unsubscribes on Dispose,
/// preventing memory leaks when NavigationView rebuilds pages on each navigation.
/// </summary>
internal sealed class DisposablePage : StackPanel, IDisposable
{
    private readonly List<Action> _unsubscribeActions = new();

    public void RegisterUnsubscribe(Action action) => _unsubscribeActions.Add(action);

    public void Dispose()
    {
        foreach (var a in _unsubscribeActions)
        {
            try { a(); } catch { }
        }
        _unsubscribeActions.Clear();
    }
}

internal sealed class SettingsWindow : Window
{
    /// <summary>Draft settings edited by the UI; committed only when 应用 is pressed.</summary>
    private readonly AppSettings _settings;
    /// <summary>The engine's live settings instance (null when there is no engine).</summary>
    private readonly AppSettings? _liveSettings;
    /// <summary>Last applied values, used by 取消更改 to roll the draft back.</summary>
    private AppSettings _applied;
    private readonly CursorEngine? _engine;
    private TextBlock? _titleBarText;
    private Border? _titleBarRoot;
    private NavigationView? _nav;
    private FrameworkElement? _currentPage;
    private string? _currentTag;
    private Border? _applyBar;
    private bool _dirty;

    // Sidebar collapse.  The template slides the pane's clip window closed in 120ms
    // and shrinks the pane to the compact strip, but it does both inside that same
    // 120ms, so the slide itself is invisible.  Fighting that animation (holding its
    // clip window open frame by frame) is what made the pane flicker, so the pane's
    // own width is animated with the same timing and curve instead: the two move
    // together and there is nothing left to fight over.
    private const int SidebarCloseMs = 120;
    private static readonly Windows.Foundation.Point SidebarSpline1 = new(0.1, 0.9);
    private static readonly Windows.Foundation.Point SidebarSpline2 = new(0.2, 1.0);
    private bool _paneAnimating;
    private Storyboard? _sidebarAnimation;
    private FrameworkElement? _sidebarPinnedPane;

    private readonly Dictionary<string, double> _scrollCache = new();

    public SettingsWindow(CursorEngine? engine = null)
    {
        _engine = engine;

        // The UI edits a draft clone, never the engine's live instance and never
        // the file on disk.  That keeps slider drags smooth (no page rebuild per
        // tick) and lets 应用 / 取消更改 decide when anything really changes.
        _liveSettings = engine?.GetSettings();
        _settings = _liveSettings?.Clone() ?? AppSettings.Load();
        _applied = _settings.Clone();
        Title = "osu! Cursor 设置";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)Math.Clamp(_settings.WindowWidth, 480, 2560),
            (int)Math.Clamp(_settings.WindowHeight, 360, 1440)));

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
            Foreground = GetTitleBarForeground()
        };
        _titleBarRoot.Child = _titleBarText;
        Grid.SetRow(_titleBarRoot, 0);

        var nav = new NavigationView
        {
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact,
            OpenPaneLength = 200,
            CompactPaneLength = 48,
            IsPaneOpen = false,
        };
        _nav = nav;
        HookSidebarAnimation();

        nav.MenuItems.Add(new NavigationViewItem { Content = "外观", Icon = new SymbolIcon(Symbol.View), Tag = "appearance" });
        nav.MenuItems.Add(new NavigationViewItem { Content = "光标", Icon = new SymbolIcon(Symbol.Target), Tag = "cursor" });
        nav.MenuItems.Add(new NavigationViewItem { Content = "场景对齐", Icon = new SymbolIcon(Symbol.AlignCenter), Tag = "align" });
        nav.MenuItems.Add(new NavigationViewItem { Content = "音效", Icon = new SymbolIcon(Symbol.Audio), Tag = "sound" });
        nav.MenuItems.Add(new NavigationViewItem { Content = "系统", Icon = new SymbolIcon(Symbol.Setting), Tag = "system" });

        nav.SelectionChanged += (s, e) =>
        {
            if (nav.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            {
                // 释放旧页面（解除事件订阅，防止内存泄漏）
                if (_currentPage is IDisposable oldPage)
                    oldPage.Dispose();

                // 缓存旧页面滚动位置
                CacheScrollPosition();

                _currentTag = tag;
                _currentPage = BuildPage(tag);
                nav.Content = _currentPage;

                // 恢复滚动位置
                RestoreScrollPosition(tag, _currentPage);
            }
        };

        nav.Loaded += (_, _) =>
        {
            try { nav.SelectedItem = nav.MenuItems[0]; }
            catch (Exception ex) { AppLog.Log($"nav.Loaded set SelectedItem failed: {ex.Message}"); }
            ApplyAppearance();
        };

        Grid.SetRow(nav, 1);

        // Floating apply bar: overlaid on the nav row (bottom-right) so the
        // sidebar still spans the full window height.
        _applyBar = BuildApplyBar();
        Grid.SetRow(_applyBar, 1);

        root.Children.Add(_titleBarRoot);
        root.Children.Add(nav);
        root.Children.Add(_applyBar);
        Content = root;
    }

    /// <summary>
    /// Bottom-right 取消更改 / 应用 bar.  Floats above the page content and
    /// stays collapsed until a setting is edited.
    /// </summary>
    private Border BuildApplyBar()
    {
        var applyBtn = new Button { Content = "应用", MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Right };
        try
        {
            if (Application.Current.Resources["AccentButtonStyle"] is Style accent)
                applyBtn.Style = accent;
        }
        catch (Exception ex)
        {
            AppLog.Log($"AccentButtonStyle unavailable: {ex.Message}");
        }
        applyBtn.Click += (_, _) => ApplyPendingChanges();

        var cancelBtn = new Button { Content = "取消更改", MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Right };
        cancelBtn.Click += (_, _) => CancelPendingChanges();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(applyBtn);

        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 24, 16),
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(8),
            Background = GetFloatingBarBrush(),
            Child = buttons,
            Visibility = Visibility.Collapsed
        };
    }

    /// <summary>
    /// Track the pane opening and closing.  Opening only has to drop the width the
    /// collapse pinned; closing is animated here, because the SplitView's own close
    /// shrinks the pane before its slide can be seen.
    /// </summary>
    private void HookSidebarAnimation()
    {
        var nav = _nav;
        if (nav == null) return;

        nav.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) =>
        {
            if (nav.IsPaneOpen) ResetSidebarWidth();
            else AnimateSidebarCollapse();
        });
    }

    /// <summary>Drop the width the collapse pinned, so the pane can widen again.</summary>
    private void ResetSidebarWidth()
    {
        _paneAnimating = false;

        try { _sidebarAnimation?.Stop(); } catch { }
        _sidebarAnimation = null;

        if (_sidebarPinnedPane != null)
        {
            try { _sidebarPinnedPane.ClearValue(FrameworkElement.WidthProperty); } catch { }
            _sidebarPinnedPane = null;
        }
    }

    private void AnimateSidebarCollapse()
    {
        if (_paneAnimating) return;
        _paneAnimating = true;

        try
        {
            var splitView = FindSplitViewPane(_nav);
            if (splitView?.Pane is not FrameworkElement pane || splitView.CompactPaneLength <= 0)
            {
                _paneAnimating = false;
                return;
            }

            ResetSidebarWidth();

            var startWidth = pane.ActualWidth > splitView.CompactPaneLength
                ? pane.ActualWidth
                : splitView.OpenPaneLength;

            // Pin the width: the SplitView arranges the pane down to the compact
            // width the moment the close lands, and only an explicit width — which
            // the animation then drives — keeps it wide enough to be seen shrinking.
            pane.Width = startWidth;
            _sidebarPinnedPane = pane;

            _sidebarAnimation = BuildSidebarAnimation(pane, "Width", startWidth, splitView.CompactPaneLength, SidebarCloseMs);
            _sidebarAnimation.Completed += (_, _) =>
            {
                try { _sidebarAnimation?.Stop(); } catch { }
                _sidebarAnimation = null;
                _paneAnimating = false;
                AppLog.Log("Sidebar collapse: done");
            };
            _sidebarAnimation.Begin();
            AppLog.Log($"Sidebar collapse: width {startWidth:0.#} -> {splitView.CompactPaneLength:0.#} over {SidebarCloseMs}ms");
        }
        catch (Exception ex)
        {
            AppLog.Log($"Sidebar collapse failed: {ex.Message}");
            _paneAnimating = false;
        }
    }

    private static Storyboard BuildSidebarAnimation(DependencyObject target, string property, double from, double to, int durationMs)
    {
        var animation = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = from });
        animation.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = TimeSpan.FromMilliseconds(durationMs),
            // The template's own curve: quick off the mark, easing out.
            KeySpline = new KeySpline { ControlPoint1 = SidebarSpline1, ControlPoint2 = SidebarSpline2 },
            Value = to
        });
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        return storyboard;
    }

    /// <summary>
    /// Sidebar (pane) styling: background follows theme, 12px rounded corners.
    /// </summary>
    private void SyncSidebarBackground()
    {
        try
        {
            var splitView = FindSplitViewPane(_nav);
            if (splitView?.Pane is not FrameworkElement pane)
            {
                AppLog.Log($"SyncSidebarBackground: SplitView.Pane not found (sv={splitView != null})");
                return;
            }

            var isDark = IsDarkTheme();
            var bg = new SolidColorBrush(isDark
                ? Color.FromArgb(255, 0x2D, 0x2D, 0x2D)
                : Colors.White);

            if (pane is Panel panel)
            {
                panel.Background = bg;
            }
            else if (pane is Border border)
            {
                border.Background = bg;
                // Left side square (fills window edge), right side rounded
                border.CornerRadius = new CornerRadius(0, 12, 12, 0);
            }
            else
            {
                AppLog.Log($"SyncSidebarBackground: pane type {pane.GetType().Name} is not Panel/Border");
            }

            // The NavigationView template insets the pane (4px vertically plus a
            // 1px bordered host), which left thin slits above and below the
            // sidebar.  Flatten the pane and its pane-side ancestors so the
            // background reaches the title bar bottom and the window bottom.
            // The template host Border already insets the pane by 1px on every
            // side; that hairline inset is kept (only the extra 3px margin the
            // pane carried is removed by the flattening below).
            pane.Margin = new Thickness(0);
            FlattenPaneAncestors(pane, splitView);

            // Rounded clip for non-Border panes
            ApplyRoundedClip(pane);
        }
        catch (Exception ex)
        {
            AppLog.Log($"SyncSidebarBackground failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Remove the template's insets (margin / padding / border / background) from
    /// the pane's ancestors up to the SplitView, so the sidebar background spans
    /// the full pane column with no hairline gaps at top or bottom.
    /// </summary>
    private static void FlattenPaneAncestors(DependencyObject pane, DependencyObject? stopAt)
    {
        try
        {
            var parent = VisualTreeHelper.GetParent(pane);
            while (parent != null && parent != stopAt)
            {
                switch (parent)
                {
                    case Border border:
                        border.Margin = new Thickness(0);
                        border.Padding = new Thickness(0);
                        border.BorderThickness = new Thickness(0);
                        border.Background = new SolidColorBrush(Colors.Transparent);
                        break;
                    case Panel p:
                        p.Margin = new Thickness(0);
                        p.Background = new SolidColorBrush(Colors.Transparent);
                        break;
                    case ContentPresenter cp:
                        cp.Margin = new Thickness(0);
                        break;
                }

                parent = VisualTreeHelper.GetParent(parent);
            }
        }
        catch (Exception ex)
        {
            AppLog.Log($"FlattenPaneAncestors failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Clear the sidebar pane background so Mica/Acrylic backdrop shows through.
    /// </summary>
    private static void ClearPaneBackground()
    {
        try
        {
            // No-op: SyncSidebarBackground already skips Mica/Acrylic mode
        }
        catch (Exception ex)
        {
            AppLog.Log($"ClearPaneBackground failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Recursively clear all background brushes inside NavigationView,
    /// so the Mica/Acrylic backdrop shows through every element.
    /// </summary>
    private static void ClearNavigationViewBackgrounds(DependencyObject element)
    {
        try
        {
            int count = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(element, i);

                // Skip the sidebar pane itself (handled separately)
                // and any NavigationViewItem containers (they need their own background)
                if (child is Panel panel)
                {
                    // Clear background of Border/Grid panels in the header/content areas
                    if (child is Border border && border.Background != null)
                    {
                        // Only clear if not a title bar element
                        border.Background = new SolidColorBrush(Colors.Transparent);
                    }
                    else if (child is Grid grid && grid.Background != null)
                    {
                        // Don't clear the main content grid (it should stay transparent)
                        // Only clear header/content host backgrounds
                        if (panel.Name == "ContentFrame" || panel.Name == "ContentGrid" ||
                            panel.Name == "HeaderClipper" || panel.Name == "PaneContentGrid")
                        {
                            grid.Background = new SolidColorBrush(Colors.Transparent);
                        }
                    }
                }

                // Recurse
                ClearNavigationViewBackgrounds(child);
            }
        }
        catch (Exception ex)
        {
            AppLog.Log($"ClearNavigationViewBackgrounds failed: {ex.Message}");
        }
    }

    private static SplitView? FindSplitViewPane(DependencyObject? parent)
    {
        if (parent == null) return null;

        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is SplitView sv)
                return sv;

            var result = FindSplitViewPane(child);
            if (result != null)
                return result;
        }
        return null;
    }

    /// <summary>
    /// Apply a rounded clip to the pane's visual (Composition layer,
    /// because WinUI RectangleGeometry has no rounded radii).
    /// Left side square (0) to fill the window edge, right side 12px rounded.
    /// </summary>
    private static void ApplyRoundedClip(FrameworkElement pane)
    {
        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(pane).Compositor;
            var clip = compositor.CreateRectangleClip();
            clip.TopLeftRadius = new Vector2(0, 0);
            clip.TopRightRadius = new Vector2(12, 12);
            clip.BottomLeftRadius = new Vector2(0, 0);
            clip.BottomRightRadius = new Vector2(12, 12);
            SyncClipBounds(clip, pane);
            ElementCompositionPreview.GetElementVisual(pane).Clip = clip;

            pane.SizeChanged += (s, e) =>
            {
                try { SyncClipBounds(clip, pane); }
                catch (Exception ex) { AppLog.Log($"ApplyRoundedClip SizeChanged failed: {ex.Message}"); }
            };
        }
        catch (Exception ex)
        {
            AppLog.Log($"ApplyRoundedClip failed: {ex.Message}");
        }
    }

    private static void SyncClipBounds(Microsoft.UI.Composition.RectangleClip clip, FrameworkElement pane)
    {
        // Guard against a collapsed layout: clipping to a zero-sized box hides the
        // sidebar entirely, which showed up as a few frames of nothing while the
        // pane's visual states swapped.
        var width = pane.ActualWidth;
        var height = pane.ActualHeight;
        if (!(width > 0) || !(height > 0)) return;

        clip.Left = 0f;
        clip.Top = 0f;
        clip.Right = (float)width;
        clip.Bottom = (float)height;
    }

    private bool IsDarkTheme() =>
        _settings.Theme == AppSettings.ThemeMode.Dark ||
        (_settings.Theme == AppSettings.ThemeMode.FollowSystem && IsSystemDark());

    private Brush GetTitleBarForeground()
    {
        return IsDarkTheme() ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Colors.Black);
    }

    private Brush GetTitleBarBrush()
    {
        // The window opacity slider is inert under Mica/Acrylic (the backdrop
        // owns the surface), so the title bar stays fully opaque there.
        var windowOpacity = _settings.BackgroundBlur == AppSettings.BlurMode.Default
            ? _settings.WindowOpacity
            : 1.0;

        var titleBarOpacity = windowOpacity <= 0.9
            ? Math.Clamp(windowOpacity + 0.1, 0, 1)
            : windowOpacity;

        var isDark = _settings.Theme == AppSettings.ThemeMode.Dark ||
                     (_settings.Theme == AppSettings.ThemeMode.FollowSystem && IsSystemDark());

        var color = isDark
            ? Color.FromArgb((byte)(titleBarOpacity * 255), 0x2D, 0x2D, 0x2D)
            : Color.FromArgb((byte)(titleBarOpacity * 255), 0xF3, 0xF3, 0xF3);

        return new SolidColorBrush(color);
    }

    /// <summary>
    /// Caption buttons (minimize / maximize / close) must follow the in-app
    /// theme rather than the system theme, otherwise light mode draws white
    /// glyphs on a light title bar.
    /// </summary>
    private void ApplyCaptionButtonColors()
    {
        try
        {
            var titleBar = AppWindow.TitleBar;
            var isDark = IsDarkTheme();

            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = isDark ? Colors.White : Colors.Black;
            titleBar.ButtonInactiveForegroundColor = isDark
                ? Color.FromArgb(0xFF, 0x7A, 0x7A, 0x7A)
                : Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A);
            titleBar.ButtonHoverForegroundColor = isDark ? Colors.White : Colors.Black;
            titleBar.ButtonHoverBackgroundColor = isDark
                ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x18, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedForegroundColor = isDark ? Colors.White : Colors.Black;
            titleBar.ButtonPressedBackgroundColor = isDark
                ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x10, 0x00, 0x00, 0x00);
        }
        catch (Exception ex)
        {
            AppLog.Log($"ApplyCaptionButtonColors failed: {ex.Message}");
        }
    }

    /// <summary>Translucent card brush for the floating 应用 / 取消更改 bar.</summary>
    private Brush GetFloatingBarBrush() =>
        IsDarkTheme()
            ? new SolidColorBrush(Color.FromArgb(0xE6, 0x2D, 0x2D, 0x2D))
            : new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));

    private FrameworkElement BuildPage(string tag)
    {
        FrameworkElement page = tag switch
        {
            "appearance" => BuildAppearancePage(),
            "cursor" => BuildCursorPage(),
            "align" => BuildAlignPage(),
            "sound" => BuildSoundPage(),
            "system" => BuildSystemPage(),
            _ => new TextBlock { Text = tag }
        };

        // Wrap in ScrollViewer for scroll position caching
        var scrollViewer = new ScrollViewer
        {
            Content = page,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        // Track scroll position changes
        scrollViewer.ViewChanged += (_, _) =>
        {
            if (_currentTag != null)
                _scrollCache[_currentTag] = scrollViewer.VerticalOffset;
        };

        return scrollViewer;
    }

    private void CacheScrollPosition()
    {
        if (_currentPage is ScrollViewer sv && _currentTag != null)
        {
            _scrollCache[_currentTag] = sv.VerticalOffset;
        }
    }

    private void RestoreScrollPosition(string tag, FrameworkElement page)
    {
        if (page is ScrollViewer sv && _scrollCache.TryGetValue(tag, out var offset))
        {
            sv.ScrollToVerticalOffset(offset);
        }
    }

    private DisposablePage BuildAppearancePage()
    {
        var panel = new DisposablePage { Spacing = 12, Padding = new Thickness(24, 16, 24, 16) };
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

        themeFollowRadio.Checked += (_, _) => { _settings.Theme = AppSettings.ThemeMode.FollowSystem; MarkDirty(); };
        themeLightRadio.Checked += (_, _) => { _settings.Theme = AppSettings.ThemeMode.Light; MarkDirty(); };
        themeDarkRadio.Checked += (_, _) => { _settings.Theme = AppSettings.ThemeMode.Dark; MarkDirty(); };

        panel.RegisterUnsubscribe(() => themeFollowRadio.Checked -= (_, _) => { });
        panel.RegisterUnsubscribe(() => themeLightRadio.Checked -= (_, _) => { });
        panel.RegisterUnsubscribe(() => themeDarkRadio.Checked -= (_, _) => { });

        themePanel.Children.Add(themeFollowRadio);
        themePanel.Children.Add(themeLightRadio);
        themePanel.Children.Add(themeDarkRadio);
        panel.Children.Add(themePanel);

        // Window opacity: Slider + TextBox + buttons
        var opacityLabel = new TextBlock { FontWeight = FontWeights.SemiBold };
        panel.Children.Add(opacityLabel);
        var opacityRow = BuildSliderWithTextBox("窗口不透明度", _settings.WindowOpacity, 0.3, 1.0,
            v => { _settings.WindowOpacity = v; opacityLabel.Text = $"窗口不透明度: {v:P0}"; MarkDirty(); },
            step: 0.05, format: "0%");
        panel.Children.Add(opacityRow);

        // Mica/Acrylic own the window surface, so the slider is locked at 100%
        // there; only 默认 (no backdrop) lets the user dim the window.
        void UpdateOpacityRow()
        {
            var locked = _settings.BackgroundBlur != AppSettings.BlurMode.Default;
            foreach (var child in opacityRow.Children)
            {
                if (child is Control control) control.IsEnabled = !locked;
            }
            opacityLabel.Text = locked
                ? "窗口不透明度: 100%（云母/亚克力模式固定）"
                : $"窗口不透明度: {_settings.WindowOpacity:P0}";
        }
        UpdateOpacityRow();

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

        blurDefaultRadio.Checked += (_, _) => { _settings.BackgroundBlur = AppSettings.BlurMode.Default; UpdateOpacityRow(); MarkDirty(); };
        blurMicaRadio.Checked += (_, _) => { _settings.BackgroundBlur = AppSettings.BlurMode.Mica; UpdateOpacityRow(); MarkDirty(); };
        blurAcrylicRadio.Checked += (_, _) => { _settings.BackgroundBlur = AppSettings.BlurMode.Acrylic; UpdateOpacityRow(); MarkDirty(); };

        panel.RegisterUnsubscribe(() => blurDefaultRadio.Checked -= (_, _) => { });
        panel.RegisterUnsubscribe(() => blurMicaRadio.Checked -= (_, _) => { });
        panel.RegisterUnsubscribe(() => blurAcrylicRadio.Checked -= (_, _) => { });

        blurPanel.Children.Add(blurDefaultRadio);
        blurPanel.Children.Add(blurMicaRadio);
        blurPanel.Children.Add(blurAcrylicRadio);
        panel.Children.Add(blurPanel);

        // Blur radius slider (pixel radius, slider 0-255, text can go up to 1024)
        var blurRadiusLabel = new TextBlock { Text = $"模糊半径: {_settings.BackgroundBlurRadius}px", FontWeight = FontWeights.SemiBold };
        panel.Children.Add(blurRadiusLabel);
        panel.Children.Add(BuildSliderWithTextBox("模糊半径", _settings.BackgroundBlurRadius, 0, 255,
            v => { _settings.BackgroundBlurRadius = (int)v; blurRadiusLabel.Text = $"模糊半径: {(int)v}px"; MarkDirty(); },
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
        RoutedEventHandler selectHandler = (_, _) =>
        {
            var path = ShowImagePicker();
            if (!string.IsNullOrEmpty(path))
            {
                _settings.BackgroundImagePath = path;
                bgPathLabel.Text = Path.GetFileName(path);
                MarkDirty();
            }
        };
        selectBgBtn.Click += selectHandler;
        panel.RegisterUnsubscribe(() => selectBgBtn.Click -= selectHandler);

        var clearBgBtn = new Button { Content = "恢复默认" };
        RoutedEventHandler clearHandler = (_, _) =>
        {
            _settings.BackgroundImagePath = AppSettings.DefaultBackgroundPath;
            bgPathLabel.Text = Path.GetFileName(AppSettings.DefaultBackgroundPath);
            MarkDirty();
        };
        clearBgBtn.Click += clearHandler;
        panel.RegisterUnsubscribe(() => clearBgBtn.Click -= clearHandler);

        bgPanel.Children.Add(bgPathLabel);
        bgPanel.Children.Add(selectBgBtn);
        bgPanel.Children.Add(clearBgBtn);
        panel.Children.Add(bgPanel);

        // Background image opacity: Slider + TextBox + buttons
        var bgOpacityLabel = new TextBlock { Text = $"背景图片不透明度: {_settings.BackgroundImageOpacity:P0}", FontWeight = FontWeights.SemiBold };
        panel.Children.Add(bgOpacityLabel);
        panel.Children.Add(BuildSliderWithTextBox("背景图片不透明度", _settings.BackgroundImageOpacity, 0.0, 1.0,
            v => { _settings.BackgroundImageOpacity = v; bgOpacityLabel.Text = $"背景图片不透明度: {v:P0}"; MarkDirty(); },
            step: 0.05, format: "0%"));

        return panel;
    }

    /// <summary>
    /// Commit every pending edit: mirror the draft onto the engine's live
    /// settings, let the engine re-apply, refresh the window, then persist.
    /// </summary>
    private void ApplyPendingChanges()
    {
        _liveSettings?.CopyFrom(_settings);

        // Engine-side side effects that used to run per slider tick.
        _engine?.ApplyCursorWidth(_settings.CursorWidth);
        _engine?.ApplyDcSceneTuning();
        _engine?.RefreshNormalSceneTuning();
        _engine?.SetTapSoundEnabled(_settings.TapSoundEnabled);
        _engine?.SetHoverSoundEnabled(_settings.HoverSoundEnabled);

        ApplyAppearanceCore();

        _settings.Save();
        _applied = _settings.Clone();
        HideApplyBar();
        AppLog.Log("Settings applied by user");
    }

    /// <summary>Roll every pending edit back to the last applied state.</summary>
    private void CancelPendingChanges()
    {
        _settings.CopyFrom(_applied);
        HideApplyBar();
        // Rebuild so every control shows the restored value again.
        RebuildCurrentPage();
        AppLog.Log("Pending settings changes discarded");
    }

    /// <summary>Mark the draft dirty and reveal the 应用 / 取消更改 bar.</summary>
    private void MarkDirty()
    {
        if (_dirty) return;
        _dirty = true;
        if (_applyBar != null) _applyBar.Visibility = Visibility.Visible;
    }

    private void HideApplyBar()
    {
        _dirty = false;
        if (_applyBar != null) _applyBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>Apply appearance from the draft to the window (no disk write).</summary>
    private void ApplyAppearance()
    {
        ApplyAppearanceCore();
        _settings.Save();
        _applied = _settings.Clone();
        HideApplyBar();
    }

    private void ApplyAppearanceCore()
    {
        AppearanceManager.ApplyAll(this, _settings);

        if (_titleBarRoot != null)
        {
            _titleBarRoot.Background = GetTitleBarBrush();
        }

        if (_titleBarText != null)
        {
            _titleBarText.Foreground = GetTitleBarForeground();
        }

        // Sidebar background must follow theme changes too
        SyncSidebarBackground();

        if (_applyBar != null)
        {
            _applyBar.Background = GetFloatingBarBrush();
        }

        ApplyCaptionButtonColors();

        // Rebuild the current page so theme colors / labels stay in sync
        RebuildCurrentPage();
    }

    private void RebuildCurrentPage()
    {
        if (_currentTag != null && _currentPage != null)
        {
            CacheScrollPosition();
            if (_currentPage is IDisposable oldPage)
                oldPage.Dispose();

            var newPage = BuildPage(_currentTag);
            _nav!.Content = newPage;
            _currentPage = newPage;
            RestoreScrollPosition(_currentTag, newPage);
        }
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
    /// Build a row with: label | Slider | TextBox + / - buttons.
    /// The Slider provides quick drag adjustment within sliderMin/sliderMax.
    /// TextBox allows precise input within textMin/textMax (can exceed slider range).
    /// </summary>
    private Grid BuildSliderWithTextBox(string label, double value, double sliderMin, double sliderMax, Action<double> apply, double step = 1.0, string format = "0.##", double? textMin = null, double? textMax = null)
    {
        double tMin = textMin ?? sliderMin;
        double tMax = textMax ?? sliderMax;

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
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
        var minusText = new TextBlock { Text = "−", FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var plusText = new TextBlock { Text = "+", FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var minusBtn = new Button { Content = minusText, Width = 32, Height = 32, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 1, 0) };
        var plusBtn = new Button { Content = plusText, Width = 32, Height = 32, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 2, 0) };

        // Slider value changed -> apply immediately for live preview
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

        // +/- buttons (discrete clicks -> apply immediately)
        minusBtn.Click += (_, _) =>
        {
            var v = Math.Max(sliderMin, slider.Value - step);
            slider.Value = v;
            valueBox.Text = v.ToString(format);
            apply(v);
        };
        plusBtn.Click += (_, _) =>
        {
            var v = Math.Min(sliderMax, slider.Value + step);
            slider.Value = v;
            valueBox.Text = v.ToString(format);
            apply(v);
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

    private DisposablePage BuildCursorPage()
    {
        var panel = new DisposablePage { Spacing = 16, Padding = new Thickness(24, 16, 24, 16) };
        panel.Children.Add(Header("光标外观"));

        panel.Children.Add(BuildSliderWithTextBox("光标大小", _settings.CursorWidth, 16, 64,
            v => { _settings.CursorWidth = v; MarkDirty(); },
            step: 1, format: "0.#"));

        return panel;
    }

    private DisposablePage BuildAlignPage()
    {
        var panel = new DisposablePage { Spacing = 16, Padding = new Thickness(24, 16, 24, 16) };
        panel.Children.Add(Header("场景对齐"));

        panel.Children.Add(new TextBlock { Text = "主窗口场景", FontWeight = FontWeights.SemiBold, Opacity = 0.8 });
        panel.Children.Add(BuildSliderWithTextBox("热点 X", _settings.NormalHotspotX, -64, 64,
            v => { _settings.NormalHotspotX = v; MarkDirty(); }, step: 0.5));
        panel.Children.Add(BuildSliderWithTextBox("热点 Y", _settings.NormalHotspotY, -64, 64,
            v => { _settings.NormalHotspotY = v; MarkDirty(); }, step: 0.5));

        panel.Children.Add(new TextBlock { Text = "DC 场景（系统光标）", FontWeight = FontWeights.SemiBold, Opacity = 0.8, Margin = new Thickness(0, 12, 0, 0) });

        panel.Children.Add(BuildSliderWithTextBox("光标大小", _settings.DcCursorSize > 0 ? _settings.DcCursorSize : _settings.CursorWidth, 16, 64,
            v => { _settings.DcCursorSize = v; MarkDirty(); }, step: 1));
        panel.Children.Add(BuildSliderWithTextBox("热点 X", _settings.DcHotspotX, -64, 64,
            v => { _settings.DcHotspotX = v; MarkDirty(); }, step: 0.5));
        panel.Children.Add(BuildSliderWithTextBox("热点 Y", _settings.DcHotspotY, -64, 64,
            v => { _settings.DcHotspotY = v; MarkDirty(); }, step: 0.5));

        return panel;
    }

    private DisposablePage BuildSoundPage()
    {
        var panel = new DisposablePage { Spacing = 16, Padding = new Thickness(24, 16, 24, 16) };
        panel.Children.Add(Header("音效"));

        var tapToggle = new ToggleSwitch { Header = "敲击音效", IsOn = _settings.TapSoundEnabled, OnContent = "开", OffContent = "关" };
        RoutedEventHandler tapHandler = (_, _) => { _settings.TapSoundEnabled = tapToggle.IsOn; MarkDirty(); };
        tapToggle.Toggled += tapHandler;
        panel.RegisterUnsubscribe(() => tapToggle.Toggled -= tapHandler);
        panel.Children.Add(tapToggle);

        panel.Children.Add(BuildSliderWithTextBox("敲击音量", _settings.TapSoundVolume * 100, 0, 100,
            v => { _settings.TapSoundVolume = v / 100.0; MarkDirty(); }, step: 5, format: "0"));

        var hoverToggle = new ToggleSwitch { Header = "悬停音效", IsOn = _settings.HoverSoundEnabled, OnContent = "开", OffContent = "关" };
        RoutedEventHandler hoverHandler = (_, _) => { _settings.HoverSoundEnabled = hoverToggle.IsOn; MarkDirty(); };
        hoverToggle.Toggled += hoverHandler;
        panel.RegisterUnsubscribe(() => hoverToggle.Toggled -= hoverHandler);
        panel.Children.Add(hoverToggle);

        panel.Children.Add(BuildSliderWithTextBox("悬停音量", _settings.HoverSoundVolume * 100, 0, 100,
            v => { _settings.HoverSoundVolume = v / 100.0; MarkDirty(); }, step: 5, format: "0"));

        return panel;
    }

    private DisposablePage BuildSystemPage()
    {
        var panel = new DisposablePage { Spacing = 16, Padding = new Thickness(24, 16, 24, 16) };
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
        RoutedEventHandler toggleHandler = (_, _) =>
        {
            if (ServiceManager.IsRunning()) ServiceManager.Stop(); else ServiceManager.Start();
        };
        toggleServiceBtn.Click += toggleHandler;
        panel.RegisterUnsubscribe(() => toggleServiceBtn.Click -= toggleHandler);
        panel.Children.Add(toggleServiceBtn);

        var autoStartCheck = new CheckBox { Content = "开机自启服务", IsChecked = autoStartEnabled, IsEnabled = isInstalled };
        RoutedEventHandler autoStartCheckedHandler = (_, _) => { if (ServiceManager.SetAutoStart(true)) { _settings.AutoStart = true; MarkDirty(); } };
        RoutedEventHandler autoStartUncheckedHandler = (_, _) => { if (ServiceManager.SetAutoStart(false)) { _settings.AutoStart = false; MarkDirty(); } };
        autoStartCheck.Checked += autoStartCheckedHandler;
        autoStartCheck.Unchecked += autoStartUncheckedHandler;
        panel.RegisterUnsubscribe(() => autoStartCheck.Checked -= autoStartCheckedHandler);
        panel.RegisterUnsubscribe(() => autoStartCheck.Unchecked -= autoStartUncheckedHandler);
        panel.Children.Add(autoStartCheck);

        var installPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var installBtn = new Button { Content = "安装服务", MinWidth = 100, IsEnabled = !isInstalled };
        RoutedEventHandler installHandler = (_, _) => { var exePath = Environment.ProcessPath; if (!string.IsNullOrEmpty(exePath)) ServiceManager.Install(exePath); };
        installBtn.Click += installHandler;
        panel.RegisterUnsubscribe(() => installBtn.Click -= installHandler);
        installPanel.Children.Add(installBtn);

        var uninstallBtn = new Button { Content = "卸载服务", MinWidth = 100, IsEnabled = isInstalled };
        RoutedEventHandler uninstallHandler = (_, _) => ServiceManager.Uninstall();
        uninstallBtn.Click += uninstallHandler;
        panel.RegisterUnsubscribe(() => uninstallBtn.Click -= uninstallHandler);
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
