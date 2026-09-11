using SmartCon.FamilyManager.ViewModels.Cloud;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public sealed partial class CloudLoginView : DialogWindowBase
{
    public CloudLoginView(CloudLoginViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);
    }
}
