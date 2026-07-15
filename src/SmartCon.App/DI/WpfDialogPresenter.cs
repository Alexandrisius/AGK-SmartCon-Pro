#if NET48
using SmartCon.App.Diagnostics;
#endif
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Context;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;

namespace SmartCon.App.DI;

public sealed class WpfDialogPresenter : IDialogPresenter
{
    private readonly Dictionary<Type, Func<object, Window>> _mappings = [];
    private readonly IRevitContext? _revitContext;

    public WpfDialogPresenter() : this(null) { }

    public WpfDialogPresenter(IRevitContext? revitContext)
    {
        _revitContext = revitContext;
    }

    public void Register<TViewModel>(Func<TViewModel, Window> factory) where TViewModel : class
    {
        _mappings[typeof(TViewModel)] = vm => factory((TViewModel)vm);
    }

    public bool? ShowDialog<TViewModel>(TViewModel viewModel) where TViewModel : class
    {
        if (!_mappings.TryGetValue(typeof(TViewModel), out var factory))
            throw new InvalidOperationException($"No view registered for ViewModel type '{typeof(TViewModel).Name}'");
        return ShowDialogInternal(factory(viewModel), _revitContext);
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
        using var _factoryMs = SmartConLogger.Measure("DlgPresenter.Factory");
        try
        {
            SmartConLogger.Freeze($"Creating view for '{vmType.Name}' (factory call)");
            window = factory(viewModel);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                $"View factory for '{vmType.Name}' THREW: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
        SmartConLogger.Freeze($"View created for '{vmType.Name}' (type={window.GetType().Name})");

        using var _showMs = SmartConLogger.Measure("DlgPresenter.ShowDialog");
        try
        {
            return ShowDialogInternal(window, _revitContext);
        }
        finally
        {
            SmartConLogger.Freeze($"ShowDialog returned for '{vmType.Name}'");
        }
    }

    /// <summary>
    /// Logs which SmartCon.FamilyManager assemblies are loaded — helps diagnose
    /// white-dialog hangs where InitializeComponent fails silently because a
    /// ResourceDictionary reference cannot resolve on net48 (missing HelixToolkit,
    /// SharpDX, etc.). Safe to call from any thread.
    /// </summary>
    private static void LogAssemblyLoadState(string context)
    {
        try
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a =>
                {
                    var n = a.GetName().Name ?? "";
                    return n.Contains("HelixToolkit") || n.Contains("SharpDX") || n.Contains("SmartCon.FamilyManager");
                })
                .Select(a => a.GetName().Name + " " + a.GetName().Version)
                .ToList();
            SmartConLogger.Freeze(
                $"[{context}] Loaded assemblies ({loaded.Count}): " +
                string.Join(", ", loaded));
        }
        catch
        {
        }
    }

    private static bool? ShowDialogInternal(Window window, IRevitContext? revitContext)
    {
        var ownerHandle = GetOwnerHandle(revitContext);
        var helper = new WindowInteropHelper(window);
        helper.Owner = ownerHandle;

        var appCurrent = Application.Current;
        var uiDispatcher = appCurrent?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
        SmartConLogger.Freeze(
            $"Pre-ShowDialog snapshot: App.Current={(appCurrent is null ? "NULL" : "non-null")}, " +
            $"AppDispatcher.Thread={uiDispatcher.Thread.ManagedThreadId}, " +
            $"AppDispatcher.HasShutdownStarted={uiDispatcher.HasShutdownStarted}, " +
            $"currentThread={Environment.CurrentManagedThreadId}, " +
            $"window.Dispatcher.Thread={window.Dispatcher.Thread.ManagedThreadId}, " +
            $"windowType={window.GetType().Name}, ownerHandle={ownerHandle}");

        bool? result;
        try
        {
#if NET48
            using var _recovery = BatchDialogRenderRecovery.Attach(window);
#endif
            result = window.ShowDialog();
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                $"ShowDialog THREW: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            throw;
        }

        SmartConLogger.Freeze(
            $"Post-ShowDialog: ActualWidth={window.ActualWidth}, " +
            $"ActualHeight={window.ActualHeight}, " +
            $"Content={(window.Content?.GetType().Name ?? "null")}, " +
            $"DataContext={(window.DataContext?.GetType().Name ?? "null")}, " +
            $"IsActive={window.IsActive}, result={result}");

        return result;
    }

    /// <summary>
    /// Returns the Revit main window handle. Uses UIApplication.MainWindowHandle
    /// (the Autodesk-recommended API since Revit 2019 — see Autodesk forum
    /// "Addin WPF Window Stops Responding": Process.MainWindowHandle is no longer
    /// reliable since Revit 2019). Falls back to Process.MainWindowHandle when
    /// IRevitContext is not available (e.g. in unit tests or before startup).
    /// </summary>
    private static IntPtr GetOwnerHandle(IRevitContext? revitContext)
    {
        try
        {
            if (revitContext is RevitContext ctx)
            {
                var handle = ctx.GetUIApplication().MainWindowHandle;
                if (handle != IntPtr.Zero) return handle;
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Freeze(
                $"GetOwnerHandle: UIApplication.MainWindowHandle failed: {ex.GetType().Name}: {ex.Message} — falling back to Process.MainWindowHandle");
        }
        return Process.GetCurrentProcess().MainWindowHandle;
    }
}
