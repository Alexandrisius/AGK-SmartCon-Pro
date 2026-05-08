using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public sealed partial class AttributeLibraryView : DialogWindowBase
{
    public AttributeLibraryView(AttributeLibraryViewModel viewModel)
    {
        InitializeComponent();
        LanguageManager.EnsureWindowResources(this);
        DataContext = viewModel;
        BindCloseRequest(viewModel);

        Loaded += (_, _) => SetColumnHeaders();
    }

    private void SetColumnHeaders()
    {
        ColName.Header = LanguageManager.GetString(StringLocalization.Keys.FM_AL_Name);
        ColGroup.Header = LanguageManager.GetString(StringLocalization.Keys.FM_AL_Group);
        ColActive.Header = LanguageManager.GetString(StringLocalization.Keys.FM_AL_Active);
    }

    private void CheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox cb) return;
        if (cb.DataContext is not AttributeLibraryViewModel.AttributeDefinitionDraft draft) return;
        if (DataContext is not AttributeLibraryViewModel vm) return;

        bool newActive = cb.IsChecked == true;
        if (!vm.TrySetActive(draft, newActive))
        {
            cb.IsChecked = !newActive;
            e.Handled = true;
        }
    }

    protected override void OnUserInitiatedClose(CancelEventArgs e)
    {
        CustomDialogResult = false;
    }
}