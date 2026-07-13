using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace SmartCon.UI.Behaviors;

public static class TreeViewBehaviors
{
    #region SelectedItem

    public static readonly DependencyProperty SelectedItemProperty =
        DependencyProperty.RegisterAttached(
            "SelectedItem",
            typeof(object),
            typeof(TreeViewBehaviors),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedItemChanged));

    public static object GetSelectedItem(DependencyObject obj) => obj.GetValue(SelectedItemProperty);

    public static void SetSelectedItem(DependencyObject obj, object value) => obj.SetValue(SelectedItemProperty, value);

    private static void OnSelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreeView treeView) return;

        treeView.SelectedItemChanged -= OnTreeViewSelectedItemChanged;
        treeView.SelectedItemChanged += OnTreeViewSelectedItemChanged;
    }

    private static void OnTreeViewSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (sender is TreeView treeView)
        {
            SetSelectedItem(treeView, e.NewValue);
        }
    }

    #endregion

    #region RightClickSelect

    public static readonly DependencyProperty RightClickSelectProperty =
        DependencyProperty.RegisterAttached(
            "RightClickSelect",
            typeof(bool),
            typeof(TreeViewBehaviors),
            new PropertyMetadata(false, OnRightClickSelectChanged));

    public static bool GetRightClickSelect(DependencyObject obj) => (bool)obj.GetValue(RightClickSelectProperty);

    public static void SetRightClickSelect(DependencyObject obj, bool value) => obj.SetValue(RightClickSelectProperty, value);

    private static void OnRightClickSelectChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreeView treeView) return;

        treeView.PreviewMouseRightButtonDown -= OnTreeViewPreviewMouseRightButtonDown;

        if ((bool)e.NewValue)
        {
            treeView.PreviewMouseRightButtonDown += OnTreeViewPreviewMouseRightButtonDown;
        }
    }

    private static void OnTreeViewPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeView treeView) return;

        var hit = VisualTreeHelper.HitTest(treeView, e.GetPosition(treeView));
        if (hit?.VisualHit is not DependencyObject dep) return;

        var treeViewItem = FindAncestor<TreeViewItem>(dep);
        if (treeViewItem is null) return;

        treeViewItem.IsSelected = true;
        e.Handled = true;
    }

    #endregion

    #region DeselectOnEmptyClick

    public static readonly DependencyProperty DeselectOnEmptyClickProperty =
        DependencyProperty.RegisterAttached(
            "DeselectOnEmptyClick",
            typeof(bool),
            typeof(TreeViewBehaviors),
            new PropertyMetadata(false, OnDeselectOnEmptyClickChanged));

    public static bool GetDeselectOnEmptyClick(DependencyObject obj) => (bool)obj.GetValue(DeselectOnEmptyClickProperty);

    public static void SetDeselectOnEmptyClick(DependencyObject obj, bool value) => obj.SetValue(DeselectOnEmptyClickProperty, value);

    private static void OnDeselectOnEmptyClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreeView treeView) return;

        treeView.PreviewMouseDown -= OnTreeViewPreviewMouseDown;

        if ((bool)e.NewValue)
        {
            treeView.PreviewMouseDown += OnTreeViewPreviewMouseDown;
        }
    }

    private static void OnTreeViewPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeView treeView) return;
        if (e.OriginalSource is not DependencyObject dep) return;
        if (FindAncestor<TreeViewItem>(dep) is not null) return;

        if (treeView.SelectedItem is null) return;

        var container = FindTreeViewItemContainer(treeView, treeView.SelectedItem);
        container?.SetCurrentValue(TreeViewItem.IsSelectedProperty, false);
    }

    #endregion

    #region AutoScrollToSelectedItem

    public static readonly DependencyProperty AutoScrollToSelectedItemProperty =
        DependencyProperty.RegisterAttached(
            "AutoScrollToSelectedItem",
            typeof(bool),
            typeof(TreeViewBehaviors),
            new PropertyMetadata(false, OnAutoScrollToSelectedItemChanged));

    public static bool GetAutoScrollToSelectedItem(DependencyObject obj) => (bool)obj.GetValue(AutoScrollToSelectedItemProperty);

    public static void SetAutoScrollToSelectedItem(DependencyObject obj, bool value) => obj.SetValue(AutoScrollToSelectedItemProperty, value);

    private static void OnAutoScrollToSelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreeView treeView) return;

        treeView.SelectedItemChanged -= OnAutoScrollSelectedItemChanged;

        if ((bool)e.NewValue)
        {
            treeView.SelectedItemChanged += OnAutoScrollSelectedItemChanged;
        }
    }

    private static void OnAutoScrollSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (sender is not TreeView treeView) return;
        if (e.NewValue is null) return;

        treeView.Dispatcher.BeginInvoke(() =>
        {
            var container = FindTreeViewItemContainer(treeView, e.NewValue);
            container?.BringIntoView();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    #endregion

    #region SuppressHorizontalScrollOnBringIntoView

    public static readonly DependencyProperty SuppressHorizontalScrollOnBringIntoViewProperty =
        DependencyProperty.RegisterAttached(
            "SuppressHorizontalScrollOnBringIntoView",
            typeof(bool),
            typeof(TreeViewBehaviors),
            new PropertyMetadata(false, OnSuppressHorizontalScrollOnBringIntoViewChanged));

    public static bool GetSuppressHorizontalScrollOnBringIntoView(DependencyObject obj) =>
        (bool)obj.GetValue(SuppressHorizontalScrollOnBringIntoViewProperty);

    public static void SetSuppressHorizontalScrollOnBringIntoView(DependencyObject obj, bool value) =>
        obj.SetValue(SuppressHorizontalScrollOnBringIntoViewProperty, value);

    private static void OnSuppressHorizontalScrollOnBringIntoViewChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreeViewItem item) return;

        item.RequestBringIntoView -= OnTreeViewItemRequestBringIntoView;

        if ((bool)e.NewValue)
        {
            item.RequestBringIntoView += OnTreeViewItemRequestBringIntoView;
        }
    }

    private static void OnTreeViewItemRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (sender is not TreeViewItem item) return;
        if (PresentationSource.FromDependencyObject(item) is null) return;

        var treeView = FindAncestor<TreeView>(item);
        if (treeView is null) return;

        var scrollViewer = FindTreeViewScrollViewer(treeView);
        if (scrollViewer is null) return;

        var topLeft = item.TransformToAncestor(treeView).Transform(new Point(0, 0));
        var itemTop = topLeft.Y;

        if (itemTop < 0
            || itemTop + item.ActualHeight > scrollViewer.ViewportHeight
            || item.ActualHeight > scrollViewer.ViewportHeight)
        {
            // Item is not fully visible vertically; let default scrolling behavior handle it.
            return;
        }

        // Item is already fully visible vertically; prevent horizontal scrolling.
        e.Handled = true;
    }

    #endregion

    #region Helpers

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T result) return result;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static ScrollViewer? FindTreeViewScrollViewer(TreeView treeView)
    {
        if (treeView.Template is null) return null;

        return treeView.Template.FindName("_tv_scrollviewer_", treeView) as ScrollViewer;
    }

    private static TreeViewItem? FindTreeViewItemContainer(ItemsControl parent, object item)
    {
        var container = parent.ItemContainerGenerator.ContainerFromItem(item);
        if (container is TreeViewItem tvi) return tvi;

        foreach (var childItem in parent.Items)
        {
            var childContainer = parent.ItemContainerGenerator.ContainerFromItem(childItem);
            if (childContainer is TreeViewItem childTvi)
            {
                var result = FindTreeViewItemContainer(childTvi, item);
                if (result is not null) return result;
            }
        }

        return null;
    }

    #endregion
}
