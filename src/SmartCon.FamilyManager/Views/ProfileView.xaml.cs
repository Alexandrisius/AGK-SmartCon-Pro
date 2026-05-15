using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public sealed partial class ProfileView : DialogWindowBase
{
    public ProfileView(ProfileViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);
    }
}
