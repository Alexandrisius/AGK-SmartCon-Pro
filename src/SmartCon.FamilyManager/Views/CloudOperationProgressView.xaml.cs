using SmartCon.FamilyManager.ViewModels.Cloud;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public sealed partial class CloudOperationProgressView : DialogWindowBase
{
    public CloudOperationProgressView(CloudOperationProgressViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);
    }
}
