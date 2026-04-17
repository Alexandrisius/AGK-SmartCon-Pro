using System.Runtime.InteropServices;
using System.Windows;
using SmartCon.PipeConnect.Services;
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
        LanguageManager.EnsureWindowResources(this);
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
        Closing += OnClosing;
        if (GetCursorPos(out var pt)) { Left = pt.X + 10; Top = pt.Y + 10; }
    }

    private bool _closeFromViewModel;

    private void OnRequestClose()
    {
        _closeFromViewModel = true;
        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeFromViewModel) return;

        if (DataContext is MiniTypeSelectorViewModel vm)
        {
            vm.CancelCommand.Execute(null);
            e.Cancel = true;
        }
    }
}
