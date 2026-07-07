using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public partial class SharedFamiliesLoadModeDialogView : DialogWindowBase
{
    public SharedFamiliesLoadModeDialogView(SharedFamiliesLoadModeDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);
    }
}
