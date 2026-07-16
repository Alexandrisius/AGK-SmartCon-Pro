using System.Windows.Controls.Primitives;
using Microsoft.Xaml.Behaviors;
using SmartCon.FamilyManager.ViewModels;

namespace SmartCon.FamilyManager.Behaviors;

/// <summary>
/// Forwards crop-frame Thumb drag deltas to <see cref="CropAvatarViewModel.MoveFrame"/>
/// (issue #131). Clamping stays in the ViewModel / CropViewportMath.
/// </summary>
public sealed class CropFrameDragBehavior : Behavior<Thumb>
{
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
        // DragDelta is a BUBBLING routed event: corner-resize thumbs nested inside
        // the frame's template also raise it here. Handle only our own drag.
        if (!ReferenceEquals(e.OriginalSource, AssociatedObject)) return;
        if (AssociatedObject.DataContext is CropAvatarViewModel vm)
            vm.MoveFrame(e.HorizontalChange, e.VerticalChange);
    }
}
