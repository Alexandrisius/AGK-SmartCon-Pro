using SmartCon.PipeConnect.Services;
using SmartCon.PipeConnect.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;

namespace SmartCon.PipeConnect.Views;

public partial class FamilySelectorView : DialogWindowBase
{
    public FamilySelectorView(FamilySelectorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);
    }
}
