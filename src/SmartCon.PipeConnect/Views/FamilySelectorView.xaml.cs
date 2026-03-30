using System.Windows;
using System.Windows.Input;
using SmartCon.Core.Models;
using SmartCon.PipeConnect.ViewModels;

namespace SmartCon.PipeConnect.Views;

/// <summary>
/// Окно редактирования списка семейств фитингов с приоритетами.
/// Открывается из MappingEditor при нажатии кнопки [...] в ячейке семейств.
/// </summary>
public partial class FamilySelectorView : Window
{
    public FamilySelectorView(FamilySelectorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.RequestClose += accepted =>
        {
            DialogResult = accepted;
            Close();
        };
    }

    /// <summary>Возвращает результат редактирования или null если отменено.</summary>
    public IEnumerable<FittingMapping>? GetResult()
    {
        if (DialogResult != true) return null;

        var vm = (FamilySelectorViewModel)DataContext;
        return vm.GetResult();
    }
}
