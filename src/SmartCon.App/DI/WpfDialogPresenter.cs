using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using SmartCon.Core.Logging;
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
        using var _scope = SmartConLogger.BeginScope("DlgPresenter",
            ("Method", nameof(ShowDialog)),
            ("ViewModel", vmType.Name));

        if (!_mappings.TryGetValue(vmType, out var factory))
        {
            SmartConLogger.Warn(
                $"No view registered for ViewModel type '{vmType.Name}' " +
                "[Action: check ServiceRegistrar view registrations in SmartCon.App/DI/ServiceRegistrar.cs]");
            throw new InvalidOperationException($"No view registered for ViewModel type '{vmType.Name}'");
        }

        LogAssemblyLoadState(vmType.Name);

        Window window;
        var factorySw = Stopwatch.StartNew();
        try
        {
            SmartConLogger.Debug($"Creating view for '{vmType.Name}' (factory call)");
            window = factory(viewModel);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                $"View factory for '{vmType.Name}' THREW: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
        finally
        {
            factorySw.Stop();
        }
        SmartConLogger.Info($"View created for '{vmType.Name}' in {factorySw.ElapsedMilliseconds}ms (type={window.GetType().Name})");

        var showDialogSw = Stopwatch.StartNew();
        try
        {
            return ShowDialogInternal(window);
        }
        finally
        {
            showDialogSw.Stop();
            SmartConLogger.Info($"ShowDialog returned in {showDialogSw.ElapsedMilliseconds}ms for '{vmType.Name}'");
        }
    }

    /// <summary>
    /// Logs which SmartCon.FamilyManager assemblies are loaded — helps diagnose
    /// white-dialog hangs where InitializeComponent fails silently because a
    /// ResourceDictionary reference cannot resolve on net48 (missing HelixToolkit,
    /// SharpGLTF, etc.). Safe to call from any thread.
    /// </summary>
    private static void LogAssemblyLoadState(string context)
    {
        try
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a =>
                {
                    var n = a.GetName().Name ?? "";
                    return n.Contains("HelixToolkit") || n.Contains("SharpGLTF") || n.Contains("SharpDX") || n.Contains("SmartCon.FamilyManager");
                })
                .Select(a => a.GetName().Name + " " + a.GetName().Version)
                .ToList();
            SmartConLogger.Debug(
                $"[{context}] Loaded assemblies ({loaded.Count}): " +
                string.Join(", ", loaded));
        }
        catch
        {
        }
    }

    private static bool? ShowDialogInternal(Window window)
    {
        var helper = new WindowInteropHelper(window);
        helper.Owner = Process.GetCurrentProcess().MainWindowHandle;

        var appCurrent = Application.Current;
        var uiDispatcher = appCurrent?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
        SmartConLogger.Debug(
            $"Pre-ShowDialog snapshot: App.Current={(appCurrent is null ? "NULL" : "non-null")}, " +
            $"AppDispatcher.Thread={uiDispatcher.Thread.ManagedThreadId}, " +
            $"AppDispatcher.HasShutdownStarted={uiDispatcher.HasShutdownStarted}, " +
            $"currentThread={Environment.CurrentManagedThreadId}, " +
            $"window.Dispatcher.Thread={window.Dispatcher.Thread.ManagedThreadId}, " +
            $"windowType={window.GetType().Name}");

        bool? result;
        try
        {
            result = window.ShowDialog();
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                $"ShowDialog THREW: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            throw;
        }

        SmartConLogger.Debug(
            $"Post-ShowDialog: ActualWidth={window.ActualWidth}, " +
            $"ActualHeight={window.ActualHeight}, " +
            $"Content={(window.Content?.GetType().Name ?? "null")}, " +
            $"DataContext={(window.DataContext?.GetType().Name ?? "null")}, " +
            $"IsActive={window.IsActive}, result={result}");

        return result;
    }
}
