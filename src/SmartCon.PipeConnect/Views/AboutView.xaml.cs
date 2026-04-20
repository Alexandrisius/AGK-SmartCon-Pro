using SmartCon.PipeConnect.Services;
using SmartCon.PipeConnect.ViewModels;
using SmartCon.UI.Controls;

namespace SmartCon.PipeConnect.Views;

public partial class AboutView : DialogWindowBase
{
    public AboutView(AboutViewModel viewModel)
    {
        InitializeComponent();
        LanguageManager.EnsureWindowResources(this);
        DataContext = viewModel;
        BindCloseRequest(viewModel);
    }
}
