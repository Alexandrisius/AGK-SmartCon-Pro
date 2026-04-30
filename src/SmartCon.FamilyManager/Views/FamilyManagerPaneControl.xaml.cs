using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI;
using SW = System.Windows;
using SWC = System.Windows.Controls;
using SWI = System.Windows.Input;
using SWT = System.Windows.Threading;

namespace SmartCon.FamilyManager.Views;

public sealed partial class FamilyManagerPaneControl : SWC.UserControl
{
    private SW.Point _dragStartPoint;
    private bool _isDragging;
    private readonly SWT.DispatcherTimer _dragExpandTimer;
    private SWC.TreeViewItem? _dragHoverItem;

    public FamilyManagerPaneControl(FamilyManagerMainViewModel viewModel)
    {
        InitializeComponent();

        var dict = LanguageManager.GetCurrentStrings();
        if (dict is not null)
        {
            Resources.MergedDictionaries.Add(dict);
        }

        DataContext = viewModel;

        _dragExpandTimer = new SWT.DispatcherTimer(SWT.DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _dragExpandTimer.Tick += DragExpandTimer_Tick;

        CategoryTree.SelectedItemChanged += CategoryTree_SelectedItemChanged;
        CategoryTree.PreviewMouseLeftButtonDown += CategoryTree_PreviewMouseLeftButtonDown;
        CategoryTree.PreviewMouseMove += CategoryTree_PreviewMouseMove;
        CategoryTree.PreviewMouseRightButtonDown += CategoryTree_PreviewMouseRightButtonDown;
        CategoryTree.PreviewMouseDown += CategoryTree_PreviewMouseDown;

        SearchBox.TextChanged += (s, e) =>
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                ? SW.Visibility.Visible
                : SW.Visibility.Collapsed;
    }

    private void CategoryTree_SelectedItemChanged(object sender, SW.RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is FamilyManagerMainViewModel vm)
        {
            vm.SelectedTreeNode = e.NewValue as CatalogTreeNodeViewModel;
        }
    }

    private void CategoryTree_PreviewMouseLeftButtonDown(object sender, SWI.MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    private void CategoryTree_PreviewMouseRightButtonDown(object sender, SWI.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not SW.DependencyObject dep) return;
        var tvi = FindAncestor<SWC.TreeViewItem>(dep);
        if (tvi?.DataContext is FamilyLeafNodeViewModel) tvi.IsSelected = true;
    }

    private void CategoryTree_PreviewMouseDown(object sender, SWI.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not SW.DependencyObject dep) return;
        if (FindAncestor<SWC.TreeViewItem>(dep) is not null) return;

        if (CategoryTree.SelectedItem is CatalogTreeNodeViewModel selected)
            selected.IsSelected = false;
    }

    private static T? FindAncestor<T>(SW.DependencyObject current) where T : SW.DependencyObject
    {
        while (current is not null)
        {
            if (current is T result) return result;
            current = SW.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void CategoryTree_PreviewMouseMove(object sender, SWI.MouseEventArgs e)
    {
        if (e.LeftButton != SWI.MouseButtonState.Pressed || _isDragging) return;

        var position = e.GetPosition(null);
        var diff = _dragStartPoint - position;
        if (Math.Abs(diff.X) < SW.SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SW.SystemParameters.MinimumVerticalDragDistance)
            return;

        var treeView = (SWC.TreeView)sender;
        if (treeView.SelectedItem is not FamilyLeafNodeViewModel leaf) return;

        _isDragging = true;
        try
        {
            SW.DragDrop.DoDragDrop(treeView, leaf, SW.DragDropEffects.Move);
        }
        finally
        {
            _isDragging = false;
            CancelDragExpandTimer();
        }
    }

    private void TreeViewItem_DragOver(object sender, SW.DragEventArgs e)
    {
        if (sender is SWC.TreeViewItem item && item.DataContext is CategoryNodeViewModel)
        {
            e.Effects = SW.DragDropEffects.Move;
            e.Handled = true;

            if (!item.IsExpanded)
            {
                if (_dragHoverItem != item)
                {
                    _dragHoverItem = item;
                    _dragExpandTimer.Stop();
                    _dragExpandTimer.Start();
                }
            }
            else
            {
                CancelDragExpandTimer();
            }
        }
        else
        {
            e.Effects = SW.DragDropEffects.None;
            CancelDragExpandTimer();
        }
    }

    private void DragExpandTimer_Tick(object? sender, EventArgs e)
    {
        _dragExpandTimer.Stop();
        if (_dragHoverItem is not null && !_dragHoverItem.IsExpanded)
        {
            _dragHoverItem.IsExpanded = true;
        }
        _dragHoverItem = null;
    }

    private void CancelDragExpandTimer()
    {
        _dragExpandTimer.Stop();
        _dragHoverItem = null;
    }

    private async void TreeViewItem_Drop(object sender, SW.DragEventArgs e)
    {
        CancelDragExpandTimer();

        if (DataContext is not FamilyManagerMainViewModel vm) return;
        if (sender is not SWC.TreeViewItem item) return;
        if (item.DataContext is not CategoryNodeViewModel targetCategory) return;
        if (e.Data.GetData(typeof(FamilyLeafNodeViewModel)) is not FamilyLeafNodeViewModel droppedFamily) return;

        var categoryId = targetCategory.CategoryId == "__no_category__"
            ? null
            : targetCategory.CategoryId;
        await vm.MoveFamilyToCategoryAsync(droppedFamily.CatalogItemId, categoryId);
    }
}
