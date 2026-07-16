using System.Windows;
using System.Windows.Media;
using Microsoft.Xaml.Behaviors;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace SmartCon.FamilyManager.Behaviors;

/// <summary>
/// Keeps an <see cref="ImageBrush.Viewbox"/> (Rectangle fill) in sync with the
/// normalized (0..1) crop selection from the ViewModel (issue #131 rev 2 live preview).
/// The brush's Stretch=UniformToFill then shows exactly what the cover-rendered
/// 4:3 avatar will look like.
/// </summary>
public sealed class ImageBrushViewboxBehavior : Behavior<Rectangle>
{
    public static readonly DependencyProperty XProperty =
        DependencyProperty.Register(nameof(X), typeof(double), typeof(ImageBrushViewboxBehavior),
            new PropertyMetadata(0.0, OnRectChanged));

    public static readonly DependencyProperty YProperty =
        DependencyProperty.Register(nameof(Y), typeof(double), typeof(ImageBrushViewboxBehavior),
            new PropertyMetadata(0.0, OnRectChanged));

    public static readonly DependencyProperty WidthProperty =
        DependencyProperty.Register(nameof(Width), typeof(double), typeof(ImageBrushViewboxBehavior),
            new PropertyMetadata(1.0, OnRectChanged));

    public static readonly DependencyProperty HeightProperty =
        DependencyProperty.Register(nameof(Height), typeof(double), typeof(ImageBrushViewboxBehavior),
            new PropertyMetadata(1.0, OnRectChanged));

    public double X
    {
        get => (double)GetValue(XProperty);
        set => SetValue(XProperty, value);
    }

    public double Y
    {
        get => (double)GetValue(YProperty);
        set => SetValue(YProperty, value);
    }

    public double Width
    {
        get => (double)GetValue(WidthProperty);
        set => SetValue(WidthProperty, value);
    }

    public double Height
    {
        get => (double)GetValue(HeightProperty);
        set => SetValue(HeightProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        UpdateViewbox();
    }

    private static void OnRectChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ImageBrushViewboxBehavior)d).UpdateViewbox();

    private void UpdateViewbox()
    {
        if (AssociatedObject?.Fill is not ImageBrush brush) return;
        if (Width <= 0 || Height <= 0) return;
        brush.Viewbox = new Rect(X, Y, Width, Height);
    }
}
