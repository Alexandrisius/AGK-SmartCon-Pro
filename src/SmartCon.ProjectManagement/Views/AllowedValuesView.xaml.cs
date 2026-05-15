using SmartCon.UI;
using SmartCon.UI.Controls;
using SmartCon.ProjectManagement.ViewModels;

namespace SmartCon.ProjectManagement.Views;

public partial class AllowedValuesView : DialogWindowBase
{
    public AllowedValuesView(AllowedValuesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        BindCloseRequest(viewModel);
    }
}
