using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public sealed partial class StatusDetailsView : DialogWindowBase
{
    public StatusDetailsView(StatusDetailsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);
    }
}
