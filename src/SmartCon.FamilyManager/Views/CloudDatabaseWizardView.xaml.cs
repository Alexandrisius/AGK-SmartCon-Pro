using SmartCon.FamilyManager.ViewModels.Cloud;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public sealed partial class CloudDatabaseWizardView : DialogWindowBase
{
    public CloudDatabaseWizardView(CloudDatabaseWizardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);
    }
}
