namespace SmartCon.FamilyManager.Views;

public sealed partial class CloudLoginView
{
    public CloudLoginView(ViewModels.Cloud.CloudLoginViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
