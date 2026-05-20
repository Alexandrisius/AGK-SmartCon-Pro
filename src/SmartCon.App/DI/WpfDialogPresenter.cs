using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.App.DI;

public sealed class WpfDialogPresenter : IDialogPresenter
{
    private readonly Dictionary<Type, Func<object, Window>> _mappings = [];

    public void Register<TViewModel>(Func<TViewModel, Window> factory) where TViewModel : class
    {
        _mappings[typeof(TViewModel)] = vm => factory((TViewModel)vm);
    }

    public bool? ShowDialog<TViewModel>(TViewModel viewModel) where TViewModel : class
    {
        if (!_mappings.TryGetValue(typeof(TViewModel), out var factory))
            throw new InvalidOperationException($"No view registered for ViewModel type '{typeof(TViewModel).Name}'");
        return ShowDialogInternal(factory(viewModel));
    }

    public bool? ShowDialog(object viewModel)
    {
#pragma warning disable CA1510
        if (viewModel is null)
            throw new ArgumentNullException(nameof(viewModel));
#pragma warning restore CA1510

        var vmType = viewModel.GetType();
        if (!_mappings.TryGetValue(vmType, out var factory))
            throw new InvalidOperationException($"No view registered for ViewModel type '{vmType.Name}'");
        return ShowDialogInternal(factory(viewModel));
    }

    private static bool? ShowDialogInternal(Window window)
    {
        var helper = new WindowInteropHelper(window);
        helper.Owner = Process.GetCurrentProcess().MainWindowHandle;
        return window.ShowDialog();
    }
}
