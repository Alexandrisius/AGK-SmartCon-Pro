using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Autodesk.Revit.UI;
using SmartCon.App.DI;
using SmartCon.App.Ribbon;
using SmartCon.Core.Logging;
using SmartCon.Core.Services;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Threading;
using SmartCon.FamilyManager;
using SmartCon.UI;

namespace SmartCon.App;

/// <summary>
/// SmartCon Revit plugin entry point. Registers the Ribbon panel, DI container,
/// and handles self-update on startup.
/// </summary>
public sealed class App : IExternalApplication
{
    private static readonly string s_smartConDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SmartCon");

    public Result OnStartup(UIControlledApplication application)
    {
#if NETFRAMEWORK
        System.Net.ServicePointManager.SecurityProtocol |=
            System.Net.SecurityProtocolType.Tls12 |
            System.Net.SecurityProtocolType.Tls13;
#endif
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        RegisterGlobalExceptionHandlers();
        try
        {
            ApplyUpdaterSelfUpdate();
            CleanupStalePendingUpdate();
            CleanupLegacyStageFolder();
            ServiceLocator.Initialize(application);
            LanguageManager.Initialize();
            RegisterNativeLibraryResolvers();

            var fmProvider = ServiceHost.GetService<FamilyManagerPaneProvider>();
            var fmPaneId = FamilyManagerPaneIds.FamilyManagerPane;
            application.RegisterDockablePane(fmPaneId, "Family Manager", fmProvider);

            RibbonBuilder.CreateRibbon(application);

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("SmartCon - Error", $"Failed to load SmartCon:\n{ex.Message}");
            return Result.Failed;
        }
    }

    /// <summary>
    /// Pre-load the native <c>assimp.dll</c> that ships alongside the add-in
    /// (under %APPDATA%\SmartCon\2025\ for net8.0-windows OR %APPDATA%\SmartCon\2021\
    /// for net48) so that SharpAssimp's <c>[DllImport("assimp")]</c> can resolve it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why pre-load, not <c>SetDllImportResolver</c>:</b> the resolver
    /// must be registered on the SharpAssimp assembly BEFORE any P/Invoke
    /// call. But SharpAssimp has a static constructor that runs when the
    /// assembly first loads — and the <c>AssemblyLoad</c> event fires
    /// AFTER the .cctor. So the P/Invoke to <c>LoadLibrary("assimp.dll")</c>
    /// happens before the resolver callback is wired up, and the resolver is
    /// never invoked.
    /// </para>
    /// <para>
    /// Pre-loading via <c>LoadLibraryEx</c> with the full path puts the
    /// module in the process's loaded-modules table. Subsequent
    /// <c>LoadLibrary("assimp.dll")</c> calls (from SharpAssimp P/Invoke)
    /// find the already-loaded module by name and return the cached handle.
    /// This is safe and does not modify global DLL search order — it just
    /// adds one module to the process early.
    /// </para>
    /// <para>
    /// <b>Multi-version:</b> works on both net8.0-windows (Revit 2025+) and
    /// net48 (Revit 2019-2024). <c>LoadLibraryEx</c> is a Win32 API callable
    /// from both frameworks; the native assimp.dll is a single binary shipped
    /// via HelixToolkit.SharpDX.Assimp's <c>runtimes/win-x64/native/assimp.dll</c>,
    /// also published into the add-in directory by the build pipeline.
    /// </para>
    /// </remarks>
    private static void RegisterNativeLibraryResolvers()
    {
        var appDir = Path.GetDirectoryName(typeof(App).Assembly.Location);
        if (string.IsNullOrEmpty(appDir))
        {
            SmartConLogger.Warn(
                "RegisterNativeLibraryResolvers: add-in directory not found " +
                "[Action: native assimp.dll may fail to load on first 3D preview]");
            return;
        }

        var assimpNativePath = Path.Combine(appDir, "assimp.dll");
        if (!File.Exists(assimpNativePath))
        {
            SmartConLogger.Warn(
                $"RegisterNativeLibraryResolvers: assimp.dll not found at '{assimpNativePath}' " +
                "[Action: 3D preview will fall back to the 'no preview' placeholder]");
            return;
        }

        try
        {
            // LOAD_WITH_ALTERED_SEARCH_PATH (0x8) tells LoadLibraryEx to use
            // the folder of the specified file as part of the search path for
            // the DLL's own dependencies. Combined with the full path to
            // assimp.dll, this ensures assimp.dll AND its dependencies
            // (VC++ runtime, already in System32) are found.
            const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;
            var handle = LoadLibraryEx(assimpNativePath, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
            if (handle != IntPtr.Zero)
            {
                SmartConLogger.Info(
                    $"Native assimp.dll pre-loaded successfully from: {assimpNativePath}");
            }
            else
            {
                var err = Marshal.GetLastWin32Error();
                SmartConLogger.Warn(
                    $"RegisterNativeLibraryResolvers: LoadLibraryEx failed (Win32Error={err}) " +
                    $"for '{assimpNativePath}' " +
                    "[Action: 3D preview will fall back to placeholder]");
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"RegisterNativeLibraryResolvers failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: 3D preview will fall back to placeholder; other features unaffected]");
        }
    }

    /// <summary>
    /// Subscribes to global exception sinks so silent WPF / XAML / async failures
    /// show up in smartcon.log. Without this, exceptions thrown during
    /// <c>InitializeComponent</c> or in <c>Dispatcher</c> render thread are
    /// swallowed by WPF and never reach user-visible code, leaving batch import
    /// dialogs blank (white window, no error trace).
    /// </summary>
    /// <remarks>
    /// The handlers do NOT suppress exceptions (<c>e.Handled = false</c>,
    /// <c>SetErrorHandled = false</c>) — they only log so the failure is
    /// diagnosed while it still propagates to standard WPF unhandled-exception UI.
    /// </remarks>
    private static void RegisterGlobalExceptionHandlers()
    {
        var logPath = Path.Combine(Path.GetDirectoryName(typeof(App).Assembly.Location) ?? ".", "assembly-load.log");
        void Mark(string step) => File.AppendAllText(logPath,
            "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] RGEH step: " + step + "\n");

        Mark("1: about to subscribe AppDomain.UnhandledException");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                var ex = args.ExceptionObject as Exception;
                SmartConLogger.Error(
                    $"[AppDomain.UnhandledException] IsTerminating={args.IsTerminating}, " +
                    $"Type={ex?.GetType().Name ?? "?"}: {ex?.Message ?? args.ExceptionObject}");
                if (ex?.StackTrace is not null)
                    SmartConLogger.Error($"Stack: {ex.StackTrace}");
            }
            catch { /* logging must never throw */ }
        };

        Mark("2: about to subscribe TaskScheduler.UnobservedTaskException");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            try
            {
                SmartConLogger.Error(
                    $"[TaskScheduler.UnobservedTaskException] Type={args.Exception.GetType().Name}: {args.Exception.Message}\n{args.Exception.StackTrace}");
            }
            catch { }
        };

        Mark("3: about to access Dispatcher.CurrentDispatcher");
        try
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            Mark("3a: Dispatcher.CurrentDispatcher returned, thread=" + dispatcher.Thread.ManagedThreadId);
            dispatcher.UnhandledException += (_, args) =>
            {
                try
                {
                    SmartConLogger.Error(
                        $"[Dispatcher.UnhandledException] Type={args.Exception.GetType().Name}: {args.Exception.Message}\n{args.Exception.StackTrace}");
                }
                catch { }
                args.Handled = false;
            };
            Mark("3b: subscribed to Dispatcher.UnhandledException");
        }
        catch (Exception ex)
        {
            Mark("3-EX: Dispatcher access failed: " + ex.GetType().Name + ": " + ex.Message);
            SmartConLogger.Warn(
                $"RegisterGlobalExceptionHandlers: could not subscribe to Dispatcher.UnhandledException: {ex.GetType().Name}: {ex.Message} " +
                "[Action: non-critical — AppDomain.UnhandledException will still catch silent failures]");
        }
        Mark("4: RegisterGlobalExceptionHandlers done");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, BestFitMapping = false)]
    private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

    public Result OnShutdown(UIControlledApplication application)
    {
        TryLaunchUpdater();
        ServiceLocator.Dispose();
        return Result.Succeeded;
    }

    private static void ApplyUpdaterSelfUpdate()
    {
        try
        {
            var pendingDir = Path.Combine(s_smartConDir, "updater-pending");
            if (!Directory.Exists(pendingDir)) return;

            foreach (var file in Directory.GetFiles(pendingDir))
            {
                var dest = Path.Combine(s_smartConDir, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
            }

            Directory.Delete(pendingDir, true);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"App.ApplyUpdaterSelfUpdate: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CleanupStalePendingUpdate()
    {
        try
        {
            var markerPath = Path.Combine(s_smartConDir, "update-pending.json");
            if (!File.Exists(markerPath)) return;

            var stagingDir = Path.Combine(s_smartConDir, "staging", "extracted");
            if (Directory.Exists(stagingDir))
            {
                try { Directory.Delete(stagingDir, true); } catch { /* Intentional: cleanup */ }
            }

            var stagingRoot = Path.Combine(s_smartConDir, "staging");
            if (Directory.Exists(stagingRoot))
            {
                try { Directory.Delete(stagingRoot, true); } catch { /* Intentional: cleanup */ }
            }

            File.Delete(markerPath);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"App.CleanupStalePendingUpdate: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryLaunchUpdater()
    {
        try
        {
            var markerPath = Path.Combine(s_smartConDir, "update-pending.json");
            if (!File.Exists(markerPath)) return;

            var updaterPath = Path.Combine(s_smartConDir, "SmartCon.Updater.exe");
            if (!File.Exists(updaterPath))
            {
                var appDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (appDir is not null)
                {
                    var fallbackPath = Path.Combine(appDir, "SmartCon.Updater.exe");
                    if (File.Exists(fallbackPath))
                        updaterPath = fallbackPath;
                    else return;
                }
                else return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = updaterPath,
                UseShellExecute = false,
                CreateNoWindow = false
            });
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"App.TryLaunchUpdater: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// One-shot cleanup of the legacy <c>files/_stage/</c> folder used by
    /// SmartCon &lt; v2.0.0 for transient family staging. The folder has been
    /// removed from the runtime flow (see ADR-035) but may still exist on
    /// disk for users upgrading from a prior version.
    /// </summary>
    private static void CleanupLegacyStageFolder()
    {
        LegacyStageFolderCleaner.Cleanup(Path.Combine(s_smartConDir, "FamilyManager"));
    }

    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == name);
        if (loaded != null) return loaded;
        var pluginDir = Path.GetDirectoryName(typeof(App).Assembly.Location);
        if (pluginDir is null) return null;
        var path = Path.Combine(pluginDir, name + ".dll");
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }

    /// <summary>
    /// Logs every HelixToolkit/SharpDX/Assimp assembly load with a minimal
    /// stack trace so we can identify WHO and WHEN loads these heavy assemblies.
    /// HelixToolkit is suspected of hooking the WPF render thread on first load
    /// (see helix-toolkit issue #1690 D3DImage.Lock() deadlock), which can cause
    /// subsequent modal dialogs to render white on net48.
    /// Writes to a SEPARATE file (assembly-load.log) so TruncateMainLog
    /// cannot eat the log lines.
    /// </summary>
    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        try
        {
            var name = args.LoadedAssembly.GetName().Name ?? "";
            if (!name.Contains("HelixToolkit")
                && !name.Contains("SharpDX")
                && !name.Contains("Assimp"))
                return;

            var stack = new StackTrace(2, false).ToString();
            if (stack.Length > 2000) stack = stack[..2000] + "...";

            var logDir = Path.GetDirectoryName(typeof(App).Assembly.Location);
            var asmLogPath = Path.Combine(logDir ?? ".", "assembly-load.log");
            var line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] thread=" + Environment.CurrentManagedThreadId + " " +
                       $"LOADED: {name} v{args.LoadedAssembly.GetName().Version}\nStackTrace:\n{stack}\n" +
                       new string('-', 80) + "\n";
            File.AppendAllText(asmLogPath, line);
        }
        catch
        {
        }
    }
}
