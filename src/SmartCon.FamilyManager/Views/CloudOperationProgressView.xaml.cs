namespace SmartCon.FamilyManager.Views;

public sealed partial class CloudOperationProgressView
{
    public CloudOperationProgressView(ViewModels.Cloud.CloudOperationProgressViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
