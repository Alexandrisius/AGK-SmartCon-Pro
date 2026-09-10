using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

/// <summary>
/// Routing part picker dialog (ADR-072, Phase 3). I-10: code-behind holds
/// only the DataContext assignment.
/// </summary>
public sealed partial class RoutingPartPickerView : DialogWindowBase
{
    public RoutingPartPickerView(RoutingPartPickerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);
    }
}
