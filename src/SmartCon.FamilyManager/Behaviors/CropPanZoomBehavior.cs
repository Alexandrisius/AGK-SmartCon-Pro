using System.Windows;
using Microsoft.Xaml.Behaviors;
using SmartCon.FamilyManager.ViewModels;
using Cursors = System.Windows.Input.Cursors;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseWheelEventArgs = System.Windows.Input.MouseWheelEventArgs;
using Point = System.Windows.Point;

namespace SmartCon.FamilyManager.Behaviors;

/// <summary>
/// Pan &amp; zoom behavior for the avatar crop dialog viewport (issue #131):
/// mouse wheel zooms around the cursor, left-drag pans the image.
/// All state changes go through <see cref="CropAvatarViewModel"/> — no logic in code-behind (I-10).
/// </summary>
public sealed class CropPanZoomBehavior : Behavior<FrameworkElement>
{
    private const double WheelZoomFactor = 1.15;

    private bool _panning;
    private Point _lastPosition;

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.MouseWheel += OnMouseWheel;
        AssociatedObject.MouseLeftButtonDown += OnMouseLeftButtonDown;
        AssociatedObject.MouseLeftButtonUp += OnMouseLeftButtonUp;
        AssociatedObject.MouseMove += OnMouseMove;
        AssociatedObject.LostMouseCapture += OnLostMouseCapture;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.MouseWheel -= OnMouseWheel;
        AssociatedObject.MouseLeftButtonDown -= OnMouseLeftButtonDown;
        AssociatedObject.MouseLeftButtonUp -= OnMouseLeftButtonUp;
        AssociatedObject.MouseMove -= OnMouseMove;
        AssociatedObject.LostMouseCapture -= OnLostMouseCapture;
        base.OnDetaching();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (AssociatedObject.DataContext is not CropAvatarViewModel vm) return;
        var factor = e.Delta > 0 ? WheelZoomFactor : 1 / WheelZoomFactor;
        var pos = e.GetPosition(AssociatedObject);
        vm.ZoomAt(factor, pos.X, pos.Y);
        e.Handled = true;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (AssociatedObject.DataContext is not CropAvatarViewModel) return;
        _panning = true;
        _lastPosition = e.GetPosition(AssociatedObject);
        AssociatedObject.CaptureMouse();
        AssociatedObject.Cursor = Cursors.Hand;
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning || AssociatedObject.DataContext is not CropAvatarViewModel vm) return;
        var pos = e.GetPosition(AssociatedObject);
        vm.PanBy(pos.X - _lastPosition.X, pos.Y - _lastPosition.Y);
        _lastPosition = pos;
        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndPan();
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        EndPan();
    }

    private void EndPan()
    {
        if (!_panning) return;
        _panning = false;
        AssociatedObject.ReleaseMouseCapture();
        AssociatedObject.ClearValue(FrameworkElement.CursorProperty);
    }
}
