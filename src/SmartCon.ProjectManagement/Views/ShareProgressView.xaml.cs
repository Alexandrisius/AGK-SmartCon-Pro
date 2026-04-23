using SmartCon.ProjectManagement.ViewModels;
using SmartCon.UI.Controls;

namespace SmartCon.ProjectManagement.Views;

public partial class ShareProgressView : DialogWindowBase
{
    public ShareProgressView(ShareProgressViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);
    }
}
