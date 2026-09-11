namespace SmartCon.FamilyManager.Views;

public sealed partial class CloudDatabaseWizardView
{
    public CloudDatabaseWizardView(ViewModels.Cloud.CloudDatabaseWizardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
