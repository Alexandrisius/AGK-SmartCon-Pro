using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public sealed partial class FamilyManagerPaneControl : System.Windows.Controls.UserControl
{
    public FamilyManagerPaneControl(FamilyManagerMainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
