using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using SmartCon.FamilyManager.ViewModels;

namespace SmartCon.FamilyManager.Views;

public partial class InputDialogView : UserControl
{
    public InputDialogView(InputDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => InputTextBox.Focus();
    }
}
