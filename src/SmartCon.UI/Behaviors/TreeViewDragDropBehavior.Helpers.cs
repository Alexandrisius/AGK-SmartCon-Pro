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
    #region Helpers

    private static void Cleanup(DragDropState state)
    {
        CancelExpandTimer(state);
        StopAutoScrollTimer(state);
        RemoveDropAdorner(state);
        state.LastValidTarget = null;
        state.IsOverValidDropTarget = false;
        state.IsDragPressValid = false;
        
        if (state.DragAdorner != null)
        {
            var layer = AdornerLayer.GetAdornerLayer(state.DragAdorner.AdornedElement);
            layer?.Remove(state.DragAdorner);
            state.DragAdorner = null;
        }
        
        Mouse.OverrideCursor = null;
    }

    private static string? GetDisplayText(object? item)
    {
        if (item is null) return null;
        var prop = item.GetType().GetProperty("DisplayName");
        return prop?.GetValue(item)?.ToString() ?? item.ToString();
    }

    private static void AddDropAdorner(DragDropState state, TreeViewItem targetItem)
    {
        if (state.DropAdorner != null) return;
        var adornerLayer = AdornerLayer.GetAdornerLayer(targetItem);
        if (adornerLayer == null) return;
        state.DropAdorner = new DropTargetAdorner(targetItem);
        adornerLayer.Add(state.DropAdorner);
    }

    private static void RemoveDropAdorner(DragDropState state)
    {
        if (state.DropAdorner == null) return;
        var adornerLayer = AdornerLayer.GetAdornerLayer(state.DropAdorner.AdornedElement);
        adornerLayer?.Remove(state.DropAdorner);
        state.DropAdorner = null;
    }

    private static DispatcherTimer CreateExpandTimer(TreeView treeView, DragDropState state)
    {
        var delay = GetAutoExpandDelayMilliseconds(treeView);
        var timer = new DispatcherTimer(DispatcherPriority.Background, treeView.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(delay)
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (state.HoverItem is not null && !state.HoverItem.IsExpanded)
                state.HoverItem.IsExpanded = true;

            state.HoverItem = null;
        };

        return timer;
    }

    private static void CancelExpandTimer(DragDropState state)
    {
        state.ExpandTimer?.Stop();
        state.HoverItem = null;
    }

    private static bool IsMouseOverScrollbar(Visual visual, Point mousePosition)
    {
        var hit = VisualTreeHelper.HitTest(visual, mousePosition);
        if (hit is null) return false;

        var dObj = hit.VisualHit;
        while (dObj is not null)
        {
            if (dObj is ScrollBar) return true;
            if (dObj is Visual || dObj is System.Windows.Media.Media3D.Visual3D)
                dObj = VisualTreeHelper.GetParent(dObj);
            else
                dObj = LogicalTreeHelper.GetParent(dObj);
        }

        return false;
    }

    private static TreeViewItem? GetTargetTreeViewItem(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is TreeViewItem item) return item;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static TreeViewItem? GetParentTreeViewItem(TreeViewItem current)
    {
        var currentObj = (DependencyObject)current;
        while (currentObj is not null)
        {
            currentObj = VisualTreeHelper.GetParent(currentObj);
            if (currentObj is TreeViewItem item) return item;
        }

        return null;
    }

    private static ScrollViewer? GetScrollViewer(DependencyObject obj)
    {
        if (obj is ScrollViewer sv) return sv;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var child = VisualTreeHelper.GetChild(obj, i);
            var result = GetScrollViewer(child);
            if (result != null) return result;
        }

        return null;
    }

    private static void UpdateAutoScroll(TreeView treeView, DragDropState state)
    {
        if (!GetAutoScrollEnabled(treeView))
        {
            StopAutoScrollTimer(state);
            return;
        }

        var scrollViewer = GetScrollViewer(treeView);
        if (scrollViewer is null)
        {
            StopAutoScrollTimer(state);
            return;
        }

        var position = state.LastDragOverPosition;
        var viewportHeight = treeView.ActualHeight;
        var topInset = GetAutoScrollTopInset(treeView);
        var tolerance = GetAutoScrollEdgeTolerance(treeView);

        if (!DragDropAutoScrollCalculator.IsInVerticalScrollZone(position.Y, viewportHeight, topInset, tolerance))
        {
            StopAutoScrollTimer(state);
            return;
        }

        if (state.AutoScrollTimer is not null)
            return;

        var interval = GetAutoScrollInterval(treeView);
        var timer = new DispatcherTimer(DispatcherPriority.Background, treeView.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(interval)
        };

        EventHandler tickHandler = (_, _) => OnAutoScrollTick(treeView, state);
        timer.Tick += tickHandler;

        state.AutoScrollTimer = timer;
        state.AutoScrollTickHandler = tickHandler;
        state.LastAutoScrollTick = DateTimeOffset.UtcNow;
        timer.Start();
    }

    private static void OnAutoScrollTick(TreeView treeView, DragDropState state)
    {
        if (!state.IsDragging)
        {
            StopAutoScrollTimer(state);
            return;
        }

        var scrollViewer = GetScrollViewer(treeView);
        if (scrollViewer is null)
        {
            StopAutoScrollTimer(state);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var elapsed = (now - state.LastAutoScrollTick).TotalSeconds;
        state.LastAutoScrollTick = now;

        var delta = DragDropAutoScrollCalculator.ComputeVerticalDelta(
            state.LastDragOverPosition.Y,
            treeView.ActualHeight,
            GetAutoScrollTopInset(treeView),
            GetAutoScrollEdgeTolerance(treeView),
            GetAutoScrollMaxSpeed(treeView),
            elapsed);

        if (Math.Abs(delta) > double.Epsilon)
        {
            var newOffset = scrollViewer.VerticalOffset + delta;
            newOffset = Math.Max(0, newOffset);
            newOffset = Math.Min(newOffset, scrollViewer.ScrollableHeight);
            scrollViewer.ScrollToVerticalOffset(newOffset);
        }
    }

    private static void StopAutoScrollTimer(DragDropState state)
    {
        if (state.AutoScrollTimer is null) return;

        if (state.AutoScrollTickHandler is not null)
            state.AutoScrollTimer.Tick -= state.AutoScrollTickHandler;

        state.AutoScrollTimer.Stop();
        state.AutoScrollTimer = null;
        state.AutoScrollTickHandler = null;
    }

    #endregion
}
