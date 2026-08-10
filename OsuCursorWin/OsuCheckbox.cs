using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace OsuCursorWin;

internal sealed class OsuCheckbox : CheckBox
{
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(255, 102, 171));

    private readonly ScaleTransform _scaleTransform = new ScaleTransform(1, 1);
    private Border? _nub;
    private Border? _fill;

    internal OsuCheckbox()
    {
        RenderTransform = _scaleTransform;
        RenderTransformOrigin = new Point(0.5, 0.5);
        Template = BuildTemplate();
        MouseEnter += OnMouseEnter;
        MouseLeave += OnMouseLeave;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _nub = Template.FindName("Nub", this) as Border;
        _fill = Template.FindName("Fill", this) as Border;
        UpdateVisual(IsChecked == true, false);
    }

    protected override void OnChecked(RoutedEventArgs e)
    {
        base.OnChecked(e);
        UpdateVisual(true, true);
    }

    protected override void OnUnchecked(RoutedEventArgs e)
    {
        base.OnUnchecked(e);
        UpdateVisual(false, true);
    }

    private static ControlTemplate BuildTemplate()
    {
        var dock = new FrameworkElementFactory(typeof(DockPanel));

        var nub = new FrameworkElementFactory(typeof(Border), "Nub");
        nub.SetValue(Border.WidthProperty, 50.0);
        nub.SetValue(Border.HeightProperty, 15.0);
        nub.SetValue(Border.CornerRadiusProperty, new CornerRadius(7.5));
        nub.SetValue(Border.BorderBrushProperty, Brushes.White);
        nub.SetValue(Border.BorderThicknessProperty, new Thickness(3));
        nub.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        nub.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
        nub.SetValue(DockPanel.DockProperty, Dock.Right);

        var fill = new FrameworkElementFactory(typeof(Border), "Fill");
        fill.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        fill.SetValue(Border.BackgroundProperty, AccentBrush);
        fill.SetValue(Border.OpacityProperty, 0.0);
        nub.AppendChild(fill);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.MarginProperty, new Thickness(0, 0, 12, 0));

        dock.AppendChild(nub);
        dock.AppendChild(presenter);

        return new ControlTemplate(typeof(OsuCheckbox))
        {
            VisualTree = dock
        };
    }

    private void UpdateVisual(bool enabled, bool animate)
    {
        if (_nub is null || _fill is null)
        {
            return;
        }

        if (!animate)
        {
            _fill.Opacity = enabled ? 1.0 : 0.0;
            _nub.BorderThickness = enabled ? new Thickness(8.5) : new Thickness(3);
            return;
        }

        _fill.BeginAnimation(
            Border.OpacityProperty,
            new DoubleAnimation(enabled ? 1.0 : 0.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
            });

        _nub.BeginAnimation(
            Border.BorderThicknessProperty,
            new ThicknessAnimation(
                enabled ? new Thickness(8.5) : new Thickness(3),
                TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new ElasticEase
                {
                    EasingMode = EasingMode.EaseOut,
                    Oscillations = 1,
                    Springiness = 5
                }
            });
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        AnimateScale(1.04, 120, new QuinticEase { EasingMode = EasingMode.EaseOut });
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        AnimateScale(1.0, 180, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 });
    }

    private void AnimateScale(double to, int milliseconds, IEasingFunction? easing)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = easing
        };

        _scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        _scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }
}
