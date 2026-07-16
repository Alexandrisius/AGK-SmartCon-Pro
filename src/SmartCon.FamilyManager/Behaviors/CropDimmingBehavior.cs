using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Xaml.Behaviors;
using SmartCon.FamilyManager.ViewModels;

namespace SmartCon.FamilyManager.Behaviors;

/// <summary>
/// Dims everything outside the crop frame (issue #131): builds a
/// <see cref="CombinedGeometry"/> (viewport rect minus frame hole) and assigns
/// it to the attached <see cref="Path"/>. Frame geometry is bound to the ViewModel;
/// viewport size comes from <see cref="CropAvatarViewModel"/> constants.
/// </summary>
public sealed class CropDimmingBehavior : Behavior<Path>
{
    public static readonly DependencyProperty FrameXProperty =
        DependencyProperty.Register(nameof(FrameX), typeof(double), typeof(CropDimmingBehavior),
            new PropertyMetadata(0.0, OnFrameChanged));

    public static readonly DependencyProperty FrameYProperty =
        DependencyProperty.Register(nameof(FrameY), typeof(double), typeof(CropDimmingBehavior),
            new PropertyMetadata(0.0, OnFrameChanged));

    public static readonly DependencyProperty FrameWidthProperty =
        DependencyProperty.Register(nameof(FrameWidth), typeof(double), typeof(CropDimmingBehavior),
            new PropertyMetadata(CropAvatarViewModel.DefaultFrameWidth, OnFrameChanged));

    public static readonly DependencyProperty FrameHeightProperty =
        DependencyProperty.Register(nameof(FrameHeight), typeof(double), typeof(CropDimmingBehavior),
            new PropertyMetadata(CropAvatarViewModel.DefaultFrameHeight, OnFrameChanged));

    private RectangleGeometry? _outer;
    private RectangleGeometry? _hole;

    public double FrameX
    {
        get => (double)GetValue(FrameXProperty);
        set => SetValue(FrameXProperty, value);
    }

    public double FrameY
    {
        get => (double)GetValue(FrameYProperty);
        set => SetValue(FrameYProperty, value);
    }

    public double FrameWidth
    {
        get => (double)GetValue(FrameWidthProperty);
        set => SetValue(FrameWidthProperty, value);
    }

    public double FrameHeight
    {
        get => (double)GetValue(FrameHeightProperty);
        set => SetValue(FrameHeightProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        _outer = new RectangleGeometry();
        _hole = new RectangleGeometry();
        AssociatedObject.Data = new CombinedGeometry(GeometryCombineMode.Exclude, _outer, _hole);
        UpdateGeometry();
    }

    protected override void OnDetaching()
    {
        _outer = null;
        _hole = null;
        base.OnDetaching();
    }

    private static void OnFrameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CropDimmingBehavior)d).UpdateGeometry();

    private void UpdateGeometry()
    {
        if (_outer is null || _hole is null) return;
        _outer.Rect = new Rect(0, 0, CropAvatarViewModel.ViewportWidth, CropAvatarViewModel.ViewportHeight);
        _hole.Rect = new Rect(FrameX, FrameY, FrameWidth, FrameHeight);
    }
}
