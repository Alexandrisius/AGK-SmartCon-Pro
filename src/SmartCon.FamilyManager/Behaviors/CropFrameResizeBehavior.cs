using System.Windows;
using System.Windows.Controls.Primitives;
using Microsoft.Xaml.Behaviors;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.FamilyManager.ViewModels;

namespace SmartCon.FamilyManager.Behaviors;

/// <summary>
/// Forwards corner-handle drag deltas to <see cref="CropAvatarViewModel.ResizeFrame"/>
/// (free-aspect frame resize, issue #131 rev 2). The <see cref="Corner"/> property
/// tells which corner of the frame the handle represents.
/// </summary>
public sealed class CropFrameResizeBehavior : Behavior<Thumb>
{
    public static readonly DependencyProperty CornerProperty =
        DependencyProperty.Register(nameof(Corner), typeof(CropCorner), typeof(CropFrameResizeBehavior),
            new PropertyMetadata(CropCorner.BottomRight));

    public CropCorner Corner
    {
        get => (CropCorner)GetValue(CornerProperty);
        set => SetValue(CornerProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.DragDelta += OnDragDelta;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.DragDelta -= OnDragDelta;
        base.OnDetaching();
    }

    private void OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        // DragDelta is a BUBBLING routed event: ignore drags of other thumbs
        // (the frame-move thumb hosts these corner handles in its template).
        if (!ReferenceEquals(e.OriginalSource, AssociatedObject)) return;
        if (AssociatedObject.DataContext is CropAvatarViewModel vm)
            vm.ResizeFrame(Corner, e.HorizontalChange, e.VerticalChange);
    }
}
