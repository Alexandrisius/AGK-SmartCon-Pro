using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        if (e.OriginalSource is not DependencyObject dep) return;

        var treeViewItem = FindAncestor<TreeViewItem>(dep);
        if (treeViewItem is null) return;

        treeViewItem.IsSelected = true;
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
