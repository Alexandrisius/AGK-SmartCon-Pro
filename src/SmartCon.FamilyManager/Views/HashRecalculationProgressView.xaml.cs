using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public partial class HashRecalculationProgressView : DialogWindowBase
{
    public HashRecalculationProgressView(HashRecalculationProgressViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);
    }
}
