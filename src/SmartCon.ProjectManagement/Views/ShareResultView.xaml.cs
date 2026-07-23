using SmartCon.ProjectManagement.ViewModels;
using SmartCon.UI.Controls;

namespace SmartCon.ProjectManagement.Views;

public partial class ShareResultView : DialogWindowBase
{
    public ShareResultView(ShareResultViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        BindCloseRequest(viewModel);
    }
}
