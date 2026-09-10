using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public partial class CropAvatarView : DialogWindowBase
{
    public CropAvatarView(CropAvatarViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);
    }
}
