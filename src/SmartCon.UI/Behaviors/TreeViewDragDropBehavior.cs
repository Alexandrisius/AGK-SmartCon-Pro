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

    public static readonly DependencyProperty PlacementDragCommandProperty =
        DependencyProperty.RegisterAttached(
            "PlacementDragCommand",
            typeof(ICommand),
            typeof(TreeViewDragDropBehavior),
            new PropertyMetadata(null, OnCommandPropertyChanged));

    public static readonly DependencyProperty AutoScrollEnabledProperty =
        DependencyProperty.RegisterAttached(
            "AutoScrollEnabled",
            typeof(bool),
            typeof(TreeViewDragDropBehavior),
            new FrameworkPropertyMetadata(true));

    public static readonly DependencyProperty AutoScrollEdgeToleranceProperty =
        DependencyProperty.RegisterAttached(
            "AutoScrollEdgeTolerance",
            typeof(double),
            typeof(TreeViewDragDropBehavior),
            new FrameworkPropertyMetadata(40.0));

    public static readonly DependencyProperty AutoScrollMaxSpeedProperty =
        DependencyProperty.RegisterAttached(
            "AutoScrollMaxSpeed",
            typeof(double),
            typeof(TreeViewDragDropBehavior),
            new FrameworkPropertyMetadata(300.0));

    public static readonly DependencyProperty AutoScrollIntervalProperty =
        DependencyProperty.RegisterAttached(
            "AutoScrollInterval",
            typeof(double),
            typeof(TreeViewDragDropBehavior),
            new FrameworkPropertyMetadata(16.0));

    public static readonly DependencyProperty AutoScrollTopInsetProperty =
        DependencyProperty.RegisterAttached(
            "AutoScrollTopInset",
            typeof(double),
            typeof(TreeViewDragDropBehavior),
            new FrameworkPropertyMetadata(0.0));

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

    public static ICommand? GetPlacementDragCommand(DependencyObject obj)
        => (ICommand?)obj.GetValue(PlacementDragCommandProperty);

    public static void SetPlacementDragCommand(DependencyObject obj, ICommand? value)
        => obj.SetValue(PlacementDragCommandProperty, value);

    public static bool GetAutoScrollEnabled(DependencyObject obj)
        => (bool)obj.GetValue(AutoScrollEnabledProperty);

    public static void SetAutoScrollEnabled(DependencyObject obj, bool value)
        => obj.SetValue(AutoScrollEnabledProperty, value);

    public static double GetAutoScrollEdgeTolerance(DependencyObject obj)
        => (double)obj.GetValue(AutoScrollEdgeToleranceProperty);

    public static void SetAutoScrollEdgeTolerance(DependencyObject obj, double value)
        => obj.SetValue(AutoScrollEdgeToleranceProperty, value);

    public static double GetAutoScrollMaxSpeed(DependencyObject obj)
        => (double)obj.GetValue(AutoScrollMaxSpeedProperty);

    public static void SetAutoScrollMaxSpeed(DependencyObject obj, double value)
        => obj.SetValue(AutoScrollMaxSpeedProperty, value);

    public static double GetAutoScrollInterval(DependencyObject obj)
        => (double)obj.GetValue(AutoScrollIntervalProperty);

    public static void SetAutoScrollInterval(DependencyObject obj, double value)
        => obj.SetValue(AutoScrollIntervalProperty, value);

    public static double GetAutoScrollTopInset(DependencyObject obj)
        => (double)obj.GetValue(AutoScrollTopInsetProperty);

    public static void SetAutoScrollTopInset(DependencyObject obj, double value)
        => obj.SetValue(AutoScrollTopInsetProperty, value);

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
        public bool IsDragPressValid;
        public Point LastDragOverPosition;
        public bool IsDragging;
        public DispatcherTimer? ExpandTimer;
        public DispatcherTimer? AutoScrollTimer;
        public EventHandler? AutoScrollTickHandler;
        public DateTimeOffset LastAutoScrollTick;
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
        var hasPlacement = GetPlacementDragCommand(treeView) is not null;

        if (hasStart || hasDrop || hasPlacement)
            Attach(treeView);
        else
            Detach(treeView);
    }

    private static void Attach(TreeView treeView)
    {
        if (GetDragDropState(treeView) is not null) return;

        treeView.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        treeView.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        treeView.PreviewMouseMove += OnPreviewMouseMove;
        treeView.PreviewDragOver += OnPreviewDragOver;
        treeView.PreviewDragLeave += OnPreviewDragLeave;
        treeView.Drop += OnDrop;
        treeView.GiveFeedback += OnGiveFeedback;

        SetDragDropState(treeView, new DragDropState());
    }

    private static void Detach(TreeView treeView)
    {
        treeView.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        treeView.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;
        treeView.PreviewMouseMove -= OnPreviewMouseMove;
        treeView.PreviewDragOver -= OnPreviewDragOver;
        treeView.PreviewDragLeave -= OnPreviewDragLeave;
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
