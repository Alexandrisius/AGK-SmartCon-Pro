using System.Runtime.InteropServices;
using System.Windows;
using SmartCon.PipeConnect.Services;
using SmartCon.PipeConnect.ViewModels;

namespace SmartCon.PipeConnect.Views;

public partial class PipeConnectEditorView : Window
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X; public int Y; }

    private bool _closeFromViewModel;

    public PipeConnectEditorView(PipeConnectEditorViewModel viewModel)
    {
        InitializeComponent();
        LanguageManager.EnsureWindowResources(this);
        DataContext = viewModel;

        viewModel.RequestClose += OnRequestClose;

        Closing += OnClosing;

        PositionNearCursor();
    }

    private void OnRequestClose()
    {
        _closeFromViewModel = true;
        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeFromViewModel) return;

        if (DataContext is PipeConnectEditorViewModel vm && vm.IsSessionActive)
        {
            e.Cancel = true;
            if (!vm.IsBusy && !vm.IsClosing)
                Dispatcher.BeginInvoke(() => vm.Cancel());
        }
    }

    private void PositionNearCursor()
    {
        if (!GetCursorPos(out var pt)) return;

        const double winW = 520;
        const double winH = 420;
        const double gap = 40;

        var wa = SystemParameters.WorkArea;

        double left = pt.X + gap;
        if (left + winW > wa.Right)
            left = pt.X - gap - winW;

        double top = pt.Y - winH / 3.0;

        left = Math.Max(wa.Left, Math.Min(left, wa.Right - winW));
        top = Math.Max(wa.Top, Math.Min(top, wa.Bottom - winH));

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
    }
}
