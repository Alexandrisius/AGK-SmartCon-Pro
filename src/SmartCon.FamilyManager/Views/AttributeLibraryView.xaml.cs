using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SmartCon.Core.Services;
using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public sealed partial class AttributeLibraryView : DialogWindowBase
{
    private AttributeLibraryViewModel _viewModel = null!;

    public AttributeLibraryView(AttributeLibraryViewModel viewModel)
    {
        InitializeComponent();
        LanguageManager.EnsureWindowResources(this);
        _viewModel = viewModel;
        DataContext = viewModel;
        BindCloseRequest(viewModel);
    }

    private void CheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox cb) return;
        if (cb.DataContext is not AttributeLibraryViewModel.AttributeDefinitionDraft draft) return;

        bool newActive = !draft.IsActive;

        if (!newActive && !draft.IsNew && draft.BindingCount > 0)
        {
            if (!_viewModel.ConfirmDeactivation(draft.BindingCount))
            {
                cb.IsChecked = draft.IsActive;
                e.Handled = true;
                return;
            }
        }

        draft.IsActive = newActive;
        draft.IsDirty = true;
    }

    private async void AttributesGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is not AttributeLibraryViewModel.AttributeDefinitionDraft item) return;

        if (e.Column == ColName)
        {
            if (e.EditingElement is System.Windows.Controls.TextBox textBox)
            {
                var newName = textBox.Text?.Trim();
                if (newName == item.Name) return;
                item.Name = newName ?? string.Empty;
                item.IsDirty = true;
            }
        }
        else if (e.Column == ColGroup)
        {
            if (e.EditingElement is System.Windows.Controls.TextBox textBox)
            {
                var newGroup = textBox.Text?.Trim();
                var trimmed = string.IsNullOrEmpty(newGroup) ? null : newGroup;
                if (trimmed == item.Group) return;
                item.Group = trimmed;
                item.IsDirty = true;
            }
        }
    }

    protected override void OnUserInitiatedClose(CancelEventArgs e)
    {
        CustomDialogResult = false;
    }
}
