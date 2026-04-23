using System.ComponentModel;
using System.Windows;
using SmartCon.PipeConnect.Services;
using SmartCon.PipeConnect.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;
using SmartCon.UI.Native;

namespace SmartCon.PipeConnect.Views;

public partial class PipeConnectEditorView : DialogWindowBase
{
    public PipeConnectEditorView(PipeConnectEditorViewModel viewModel)
    {
        InitializeComponent();
        LanguageManager.EnsureWindowResources(this);
        DataContext = viewModel;
        BindCloseRequest(viewModel);
        PositionNearCursor();
    }

    protected override void OnUserInitiatedClose(CancelEventArgs e)
    {
        if (DataContext is PipeConnectEditorViewModel vm && vm.IsSessionActive)
        {
            e.Cancel = true;
            if (!vm.IsBusy && !vm.IsClosing)
                Dispatcher.BeginInvoke(() => vm.Cancel());
        }
    }

    private void PositionNearCursor()
    {
        var pos = CursorHelper.GetCursorPosition();
        if (pos == default) return;

        const double winW = 520;
        const double winH = 420;
        const double gap = 40;

        var wa = SystemParameters.WorkArea;

        double left = pos.X + gap;
        if (left + winW > wa.Right)
            left = pos.X - gap - winW;

        double top = pos.Y - winH / 3.0;

        left = Math.Max(wa.Left, Math.Min(left, wa.Right - winW));
        top = Math.Max(wa.Top, Math.Min(top, wa.Bottom - winH));

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
    }
}
