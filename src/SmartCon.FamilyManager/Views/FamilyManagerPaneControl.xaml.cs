using SmartCon.FamilyManager.ViewModels;

namespace SmartCon.FamilyManager.Views;

public sealed partial class FamilyManagerPaneControl : System.Windows.Controls.UserControl
{
    public FamilyManagerPaneControl(FamilyManagerMainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
