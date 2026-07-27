using System.Windows;
using System.Windows.Media;
using SmartCon.Core.Models;
using SmartCon.PipeConnect.Services;
using SmartCon.PipeConnect.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;
using SmartCon.UI.Native;

namespace SmartCon.PipeConnect.Views;

public partial class PipeConnectEditorView : DialogWindowBase, IEditorWindowBoundsAccessor
{
    public PipeConnectEditorView(PipeConnectEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);
        viewModel.WindowBoundsAccessor = this;
        PositionNearCursor();
    }

    public ScreenRect? GetWindowBoundsInPixels()
    {
        if (!IsVisible) return null;
        var dpi = VisualTreeHelper.GetDpi(this);
        return new ScreenRect(
            Left * dpi.DpiScaleX,
            Top * dpi.DpiScaleY,
            (Left + ActualWidth) * dpi.DpiScaleX,
            (Top + ActualHeight) * dpi.DpiScaleY);
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
