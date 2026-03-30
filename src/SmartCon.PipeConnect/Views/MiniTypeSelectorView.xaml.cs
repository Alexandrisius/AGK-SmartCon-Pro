using System.Runtime.InteropServices;
using System.Windows;
using SmartCon.PipeConnect.ViewModels;

namespace SmartCon.PipeConnect.Views;

public partial class MiniTypeSelectorView : Window
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X; public int Y; }

    public MiniTypeSelectorView(MiniTypeSelectorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += Close;
        if (GetCursorPos(out var pt)) { Left = pt.X + 10; Top = pt.Y + 10; }
    }
}
