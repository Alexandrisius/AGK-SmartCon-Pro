using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public sealed partial class CategoryTreeEditorView : DialogWindowBase
{
    public CategoryTreeEditorView(CategoryTreeEditorViewModel viewModel)
    {
        InitializeComponent();
        LanguageManager.EnsureWindowResources(this);
        DataContext = viewModel;
        BindCloseRequest(viewModel);

        SearchBox.TextChanged += SearchBox_TextChanged;
        UpdateSearchPlaceholder();
    }

    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is CategoryTreeEditorViewModel vm)
            vm.SelectedNode = e.NewValue as CategoryNodeViewModel;
    }

    private void TreeViewItem_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem item)
        {
            item.IsSelected = true;
            if (DataContext is CategoryTreeEditorViewModel vm && item.DataContext is CategoryNodeViewModel node)
            {
                vm.SelectedNode = node;
            }
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchPlaceholder();
    }

    private void UpdateSearchPlaceholder()
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    protected override void OnUserInitiatedClose(CancelEventArgs e)
    {
        if (DataContext is CategoryTreeEditorViewModel vm && vm.HasUnsavedChanges)
        {
            var result = vm.ConfirmUnsavedChanges();
            if (result is null)
            {
                e.Cancel = true;
            }
            else if (result == true)
            {
                vm.SaveCommand.Execute(null);
            }
        }
    }
}