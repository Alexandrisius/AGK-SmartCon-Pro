using System.ComponentModel;
using SmartCon.PipeConnect.Services;
using SmartCon.PipeConnect.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;
using SmartCon.UI.Native;

namespace SmartCon.PipeConnect.Views;

public partial class MiniTypeSelectorView : DialogWindowBase
{
    public MiniTypeSelectorView(MiniTypeSelectorViewModel viewModel)
    {
        InitializeComponent();
        LanguageManager.EnsureWindowResources(this);
        DataContext = viewModel;
        BindCloseRequest(viewModel);
        var pos = CursorHelper.GetCursorPosition();
        if (pos != default) { Left = pos.X + 10; Top = pos.Y + 10; }
    }

    protected override void OnUserInitiatedClose(CancelEventArgs e)
    {
        if (DataContext is MiniTypeSelectorViewModel vm)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(() => vm.CancelCommand.Execute(null));
        }
    }
}
