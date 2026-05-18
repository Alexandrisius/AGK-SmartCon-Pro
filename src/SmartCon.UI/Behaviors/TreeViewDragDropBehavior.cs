using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SmartCon.UI.DragDrop;

namespace SmartCon.UI.Behaviors;

public static class TreeViewDragDropBehavior
{
    #region Attached Properties

    public static readonly DependencyProperty StartDragCommandProperty =
        DependencyProperty.RegisterAttached(
            "StartDragCommand",
            typeof(ICommand),
            typeof(TreeViewDragDropBehavior),
            new PropertyMetadata(null, OnCommandPropertyChanged));

    public static readonly DependencyProperty DropCommandProperty =
        DependencyProperty.RegisterAttached(
            "DropCommand",
            typeof(ICommand),
            typeof(TreeViewDragDropBehavior),
            new PropertyMetadata(null, OnCommandPropertyChanged));

    public static readonly DependencyProperty AutoExpandDelayMillisecondsProperty =
        DependencyProperty.RegisterAttached(
            "AutoExpandDelayMilliseconds",
            typeof(double),
            typeof(TreeViewDragDropBehavior),
            new PropertyMetadata(500.0));

    public static readonly DependencyProperty ResolveParentDropTargetProperty =
        DependencyProperty.RegisterAttached(
            "ResolveParentDropTarget",
            typeof(bool),
            typeof(TreeViewDragDropBehavior),
            new PropertyMetadata(false));

    private static readonly DependencyProperty DragDropStateProperty =
        DependencyProperty.RegisterAttached(
            "DragDropState",
            typeof(DragDropState),
            typeof(TreeViewDragDropBehavior),
            new PropertyMetadata(null));

    #endregion

    #region Getters / Setters

    public static ICommand? GetStartDragCommand(DependencyObject obj)
        => (ICommand?)obj.GetValue(StartDragCommandProperty);

    public static void SetStartDragCommand(DependencyObject obj, ICommand? value)
        => obj.SetValue(StartDragCommandProperty, value);

    public static ICommand? GetDropCommand(DependencyObject obj)
        => (ICommand?)obj.GetValue(DropCommandProperty);

    public static void SetDropCommand(DependencyObject obj, ICommand? value)
        => obj.SetValue(DropCommandProperty, value);

    public static double GetAutoExpandDelayMilliseconds(DependencyObject obj)
        => (double)obj.GetValue(AutoExpandDelayMillisecondsProperty);

    public static void SetAutoExpandDelayMilliseconds(DependencyObject obj, double value)
        => obj.SetValue(AutoExpandDelayMillisecondsProperty, value);

    public static bool GetResolveParentDropTarget(DependencyObject obj)
        => (bool)obj.GetValue(ResolveParentDropTargetProperty);

    public static void SetResolveParentDropTarget(DependencyObject obj, bool value)
        => obj.SetValue(ResolveParentDropTargetProperty, value);

    private static DragDropState? GetDragDropState(DependencyObject obj)
        => (DragDropState?)obj.GetValue(DragDropStateProperty);

    private static void SetDragDropState(DependencyObject obj, DragDropState? value)
        => obj.SetValue(DragDropStateProperty, value);

    #endregion

    #region State

    private const string DragFormat = "SmartCon.TreeViewDrag";

    private sealed class DragDropState
    {
        public Point DragStartPoint;
        public bool IsDragging;
        public DispatcherTimer? ExpandTimer;
        public TreeViewItem? HoverItem;
        public DragAdorner? DragAdorner;
        public DropTargetAdorner? DropAdorner;
        public TreeViewItem? LastValidTarget;
        public bool IsOverValidDropTarget;
        public DateTimeOffset LastDragOverTime;
    }

    #endregion

    #region Attach / Detach

    private static void OnCommandPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreeView treeView) return;

        var hasStart = GetStartDragCommand(treeView) is not null;
        var hasDrop = GetDropCommand(treeView) is not null;

        if (hasStart || hasDrop)
            Attach(treeView);
        else
            Detach(treeView);
    }

    private static void Attach(TreeView treeView)
    {
        if (GetDragDropState(treeView) is not null) return;

        treeView.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        treeView.PreviewMouseMove += OnPreviewMouseMove;
        treeView.PreviewDragOver += OnPreviewDragOver;
        treeView.Drop += OnDrop;
        treeView.GiveFeedback += OnGiveFeedback;

        SetDragDropState(treeView, new DragDropState());
    }

    private static void Detach(TreeView treeView)
    {
        treeView.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        treeView.PreviewMouseMove -= OnPreviewMouseMove;
        treeView.PreviewDragOver -= OnPreviewDragOver;
        treeView.Drop -= OnDrop;
        treeView.GiveFeedback -= OnGiveFeedback;

        if (GetDragDropState(treeView) is { } state)
        {
            Cleanup(state);
            SetDragDropState(treeView, null);
        }
    }

    #endregion

    #region Event Handlers

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var treeView = (TreeView)sender;
        if (GetDragDropState(treeView) is not { } state) return;

        state.DragStartPoint = e.GetPosition(null);
    }

    private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var treeView = (TreeView)sender;
        if (GetDragDropState(treeView) is not { } state) return;
        if (state.IsDragging) return;

        var position = e.GetPosition(null);
        var diff = state.DragStartPoint - position;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var command = GetStartDragCommand(treeView);
        var draggedItem = treeView.SelectedItem;

        if (command?.CanExecute(draggedItem) != true) return;
        if (IsMouseOverScrollbar(treeView, e.GetPosition(treeView))) return;

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

    private static void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        var treeView = (TreeView)sender;
        if (GetDragDropState(treeView) is not { } state) return;
        if (!e.Data.GetDataPresent(DragFormat)) return;

        var draggedItem = e.Data.GetData(DragFormat);
        var command = GetDropCommand(treeView);
        var targetItem = GetTargetTreeViewItem(e.OriginalSource as DependencyObject);
        var dropInfo = new TreeViewDropInfo(draggedItem, targetItem?.DataContext);

        state.LastDragOverTime = DateTimeOffset.UtcNow;

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

            AutoScroll(treeView, e);

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

    #region Helpers

    private static void Cleanup(DragDropState state)
    {
        CancelExpandTimer(state);
        RemoveDropAdorner(state);
        state.LastValidTarget = null;
        state.IsOverValidDropTarget = false;
        
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

    private static void AutoScroll(TreeView treeView, DragEventArgs e)
    {
        var scrollViewer = GetScrollViewer(treeView);
        if (scrollViewer is null) return;

        const double tolerance = 30.0;
        const double offset = 15.0;
        var position = e.GetPosition(treeView);

        if (position.Y < tolerance)
        {
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - offset);
        }
        else if (position.Y > treeView.ActualHeight - tolerance)
        {
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + offset);
        }
    }

    #endregion
}
