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

    public PipeConnectEditorView(PipeConnectEditorViewModel viewModel)
    {
        InitializeComponent();
        LanguageManager.EnsureWindowResources(this);
        DataContext = viewModel;
        viewModel.RequestClose += Close;
        // Init() вызывается из PipeConnectCommand.Execute() ДО ShowDialog(),
        // чтобы вся цепочка (выравнивание, фитинг, размеры) была готова до открытия окна.
        Closing += (_, e) =>
        {
            if (viewModel.IsSessionActive && !viewModel.IsBusy && !viewModel.IsClosing)
                viewModel.Cancel();
        };
        PositionNearCursor();
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
