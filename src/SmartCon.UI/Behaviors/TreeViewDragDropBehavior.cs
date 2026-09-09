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

}
