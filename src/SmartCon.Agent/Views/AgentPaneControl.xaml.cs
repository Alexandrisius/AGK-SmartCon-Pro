using SmartCon.Agent.ViewModels;

namespace SmartCon.Agent.Views;

public sealed partial class AgentPaneControl : System.Windows.Controls.UserControl
{
    public AgentPaneControl(AgentMainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}