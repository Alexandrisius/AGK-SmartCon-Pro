using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SmartCon.Core.Logging;
using SmartCon.UI.DragDrop;

namespace SmartCon.UI.Behaviors;

public static partial class TreeViewDragDropBehavior
{
    #region Event Handlers

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var treeView = (TreeView)sender;
        if (GetDragDropState(treeView) is not { } state) return;

        state.DragStartPoint = e.GetPosition(null);

        // A drag may only be initiated by a press that landed on a tree item.
        // Presses on the scrollbar, empty padding or any chrome outside
        // TreeViewItem previously fell back to treeView.SelectedItem once the
        // drag threshold was exceeded: the user grabbed the scrollbar to scroll,
        // the cursor drifted a few px off the bar during the vertical drag,
        // IsMouseOverScrollbar (current-position check) returned false and a real
        // DnD session started, silently moving the selected family between
        // categories on release. Gate on the PRESS position instead — the
        // canonical approach (GongSolutions.WPF.DragDrop gates in
        // DragSource_PreviewMouseLeftButtonDown the same way). See #236.
        var pressPos = e.GetPosition(treeView);
        state.IsDragPressValid =
            !IsMouseOverScrollbar(treeView, pressPos) &&
            GetDraggedItemAtPosition(treeView, pressPos) is not null;

        if (!state.IsDragPressValid)
        {
            using var _scope = SmartConLogger.BeginScope("FMTree", ("Method", nameof(OnPreviewMouseLeftButtonDown)));
            SmartConLogger.Debug("Drag press rejected: press did not land on a tree item (scrollbar/padding/chrome)");
        }
    }

    private static void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var treeView = (TreeView)sender;
        if (GetDragDropState(treeView) is not { } state) return;

        // Reset the press validity so a stale DragStartPoint from a previous
        // press can never start a drag (e.g. when the press landed on a sibling
        // overlay above the TreeView and PreviewMouseLeftButtonDown never fired).
        state.IsDragPressValid = false;
    }

    private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var treeView = (TreeView)sender;
        if (GetDragDropState(treeView) is not { } state) return;
        if (state.IsDragging) return;
        if (!state.IsDragPressValid) return;

        var position = e.GetPosition(null);
        var diff = state.DragStartPoint - position;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        // Determine the dragged item by hit-testing the TreeViewItem under the cursor,
        // not by treeView.SelectedItem. Two reasons:
        //   1. The search pipeline rebuilds TreeNodes on every keystroke (debounced 300ms),
        //      so SelectedItem may point at a VM that is no longer in the new collection —
        //      FindParentOf() then fails and CanExecute returns false → drag never starts.
        //   2. Highlighted Runs inside SearchHighlightConverter's TextBlock have a non-null
        //      Background and absorb hit-test results; SelectedItem may stay null even after
        //      a click because the click never reaches the TreeViewItem's chrome.
        // Falling back to SelectedItem keeps existing behaviour for non-search flows.
        var draggedItem = GetDraggedItemUnderCursor(treeView) ?? treeView.SelectedItem;
        if (draggedItem is null) return;
        if (IsMouseOverScrollbar(treeView, e.GetPosition(treeView))) return;

        var placementCommand = GetPlacementDragCommand(treeView);
        if (placementCommand?.CanExecute(draggedItem) == true)
        {
            state.IsDragging = true;
            try
            {
                placementCommand.Execute(draggedItem);
            }
            finally
            {
                state.IsDragging = false;
                Cleanup(state);
            }
            return;
        }

        var command = GetStartDragCommand(treeView);
        if (command?.CanExecute(draggedItem) != true) return;

        state.IsDragging = true;

        try
        {
            command.Execute(draggedItem);

            var dragData = new DataObject(DragFormat, draggedItem);
            System.Windows.DragDrop.DoDragDrop(treeView, dragData, DragDropEffects.Move);
        }
        finally
        {
            state.IsDragging = false;
            Cleanup(state);
        }
    }

    /// <summary>
    /// Hit-tests the TreeView at the current cursor position. Returns the
    /// DataContext of the enclosing TreeViewItem, or null when the cursor is
    /// over a non-item area (scrollbar, padding, chrome).
    /// </summary>
    private static object? GetDraggedItemUnderCursor(TreeView treeView)
        => GetDraggedItemAtPosition(treeView, Mouse.GetPosition(treeView));

    /// <summary>
    /// Hit-tests the TreeView at the given position (TreeView coordinates) and
    /// walks up the visual tree until the enclosing <see cref="TreeViewItem"/> is
    /// found. Returns its DataContext (the VM the user is actually dragging from).
    /// Returns null when the position is over a non-item area (scrollbar, padding,
    /// chrome) so the caller can fall back to <c>treeView.SelectedItem</c>.
    /// </summary>
    private static object? GetDraggedItemAtPosition(TreeView treeView, Point pt)
    {
        var hit = VisualTreeHelper.HitTest(treeView, pt);
        var current = hit?.VisualHit as DependencyObject;
        while (current is not null && current is not TreeViewItem)
        {
            current = VisualTreeHelper.GetParent(current);
        }
        return (current as TreeViewItem)?.DataContext;
    }

    private static void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        var treeView = (TreeView)sender;
        if (GetDragDropState(treeView) is not { } state) return;
        if (!e.Data.GetDataPresent(DragFormat)) return;

        state.LastDragOverTime = DateTimeOffset.UtcNow;
        state.LastDragOverPosition = e.GetPosition(treeView);

        // Auto-scroll is independent of whether the item under the cursor is a valid drop target.
        UpdateAutoScroll(treeView, state);

        var draggedItem = e.Data.GetData(DragFormat);
        var command = GetDropCommand(treeView);
        var targetItem = GetTargetTreeViewItem(e.OriginalSource as DependencyObject);
        var dropInfo = new TreeViewDropInfo(draggedItem, targetItem?.DataContext);

        var resolvedItem = targetItem;
        var resolvedDropInfo = dropInfo;

        if (command?.CanExecute(dropInfo) != true && GetResolveParentDropTarget(treeView))
        {
            var current = targetItem;
            while (current is not null)
            {
                var parent = GetParentTreeViewItem(current);
                if (parent is null) break;
                var parentDropInfo = new TreeViewDropInfo(draggedItem, parent.DataContext);
                if (command?.CanExecute(parentDropInfo) == true)
                {
                    resolvedItem = parent;
                    resolvedDropInfo = parentDropInfo;
                    break;
                }
                current = parent;
            }
        }

        if (command?.CanExecute(resolvedDropInfo) == true)
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            state.IsOverValidDropTarget = true;

            if (state.LastValidTarget != resolvedItem)
            {
                RemoveDropAdorner(state);
                state.LastValidTarget = resolvedItem;
                if (resolvedItem != null)
                    AddDropAdorner(state, resolvedItem);
            }

            if (resolvedItem is not null && !resolvedItem.IsExpanded)
            {
                if (state.HoverItem != resolvedItem)
                {
                    state.HoverItem = resolvedItem;
                    state.ExpandTimer ??= CreateExpandTimer(treeView, state);
                    state.ExpandTimer.Stop();
                    state.ExpandTimer.Start();
                }
            }
            else
            {
                CancelExpandTimer(state);
            }
        }
        else
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            state.IsOverValidDropTarget = false;
            RemoveDropAdorner(state);
            state.LastValidTarget = null;
            CancelExpandTimer(state);
        }
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        var treeView = (TreeView)sender;
        if (GetDragDropState(treeView) is not { } state) return;
        if (!e.Data.GetDataPresent(DragFormat)) return;

        var draggedItem = e.Data.GetData(DragFormat);
        var command = GetDropCommand(treeView);
        var targetItem = GetTargetTreeViewItem(e.OriginalSource as DependencyObject);
        var dropInfo = new TreeViewDropInfo(draggedItem, targetItem?.DataContext);

        if (command?.CanExecute(dropInfo) != true && GetResolveParentDropTarget(treeView))
        {
            var current = targetItem;
            while (current is not null)
            {
                var parent = GetParentTreeViewItem(current);
                if (parent is null) break;
                var parentDropInfo = new TreeViewDropInfo(draggedItem, parent.DataContext);
                if (command?.CanExecute(parentDropInfo) == true)
                {
                    dropInfo = parentDropInfo;
                    break;
                }
                current = parent;
            }
        }

        if (command?.CanExecute(dropInfo) == true)
        {
            command.Execute(dropInfo);
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        Cleanup(state);
    }

    private static void OnPreviewDragLeave(object sender, DragEventArgs e)
    {
        var treeView = (TreeView)sender;
        if (GetDragDropState(treeView) is not { } state) return;

        StopAutoScrollTimer(state);
    }

    private static void OnGiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        var treeView = (TreeView)sender;
        if (GetDragDropState(treeView) is not { } state) return;
        
        var elapsed = DateTimeOffset.UtcNow - state.LastDragOverTime;
        if (elapsed.TotalMilliseconds > 50)
        {
            e.UseDefaultCursors = false;
            Mouse.SetCursor(Cursors.No);
        }
        else if (state.IsOverValidDropTarget)
        {
            e.UseDefaultCursors = true;
        }
        else
        {
            e.UseDefaultCursors = false;
            Mouse.SetCursor(Cursors.No);
        }
        
        if (state.DragAdorner is null)
        {
            var source = PresentationSource.FromVisual(treeView);
            if (source?.RootVisual is UIElement root)
            {
                var layer = AdornerLayer.GetAdornerLayer(root);
                if (layer != null)
                {
                    state.DragAdorner = new DragAdorner(root, GetDisplayText(treeView.SelectedItem));
                    layer.Add(state.DragAdorner);
                }
            }
        }

        if (state.DragAdorner != null)
        {
            var pos = Mouse.GetPosition(state.DragAdorner.AdornedElement);
            state.DragAdorner.UpdatePosition(pos);
        }
        
        e.Handled = true;
    }

    #endregion
}
