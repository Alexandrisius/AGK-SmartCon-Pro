using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Autodesk.Revit.UI;
using SmartCon.App.Diagnostics;
using SmartCon.App.DI;
using SmartCon.App.Ribbon;
using SmartCon.Core.Deployment;
using SmartCon.Core.Logging;
using SmartCon.Core.Services;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Threading;
using SmartCon.FamilyManager;
using SmartCon.UI;
#if NET8_0_OR_GREATER
using AppBase = Nice3point.Revit.Toolkit.External.ExternalApplication;
#else
using AppBase = Autodesk.Revit.UI.IExternalApplication;
#endif

namespace SmartCon.App;

/// <summary>
/// SmartCon Revit plugin entry point. Registers the Ribbon panel, DI container,
/// and handles self-update on startup.
/// On net8 (Revit 2025+) inherits Nice3point.Revit.Toolkit ExternalApplication so the
/// plugin runs inside the isolated 'SmartCon' AssemblyLoadContext (ADR-051, Issue #134).
/// </summary>
public sealed class App : AppBase
{
    private static readonly string s_smartConDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SmartCon");

#if NET8_0_OR_GREATER
    public override void OnStartup()
    {
        Result = OnStartupCore(Application);
    }

    public override void OnShutdown()
    {
        OnShutdownCore();
    }
#else
    public Result OnStartup(UIControlledApplication application)
    {
        return OnStartupCore(application);
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        OnShutdownCore();
        return Result.Succeeded;
    }
#endif

    private static Result OnStartupCore(UIControlledApplication application)
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
            AddinManifestHealer.EnsureCurrent(application);
            DependencyGuard.ScanLoadedAssemblies();
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

    private static void OnShutdownCore()
    {
        TryLaunchUpdater();
        ServiceLocator.Dispose();
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
                    (ex is not null ? DescribeException(ex) : args.ExceptionObject?.ToString()));
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
                    $"[TaskScheduler.UnobservedTaskException] {DescribeException(args.Exception)}\n{args.Exception.StackTrace}");
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
                        $"[Dispatcher.UnhandledException] {DescribeException(args.Exception)}\n{args.Exception.StackTrace}");
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

    /// <summary>
    /// Formats an exception with its full inner-exception chain — the outer
    /// exception alone (e.g. XamlParseException) hides the real root cause
    /// (see Issue #134, R23 ViewBoxModel3D crash: XamlParseException →
    /// TypeInitializationException → FileLoadException 0x80131044).
    /// </summary>
    private static string DescribeException(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        for (var cur = ex; cur != null; cur = cur.InnerException)
        {
            if (!ReferenceEquals(cur, ex))
                sb.Append(" ---> ");
            sb.Append(cur.GetType().Name).Append(": ").Append(cur.Message);
        }
        return sb.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, BestFitMapping = false)]
    private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

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

#if NETFRAMEWORK
    private static readonly Lazy<HashSet<string>> s_mergedAssemblyNames = new(LoadMergedAssemblyNames);

    /// <summary>
    /// Reads the embedded merged-dependencies.txt (ADR-051): the single source of
    /// truth for which third-party assemblies are ILRepack-merged into
    /// SmartCon.Dependencies.dll on net48.
    /// </summary>
    private static HashSet<string> LoadMergedAssemblyNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var assembly = typeof(App).Assembly;
            using var stream = assembly.GetManifestResourceStream("SmartCon.App.Resources.merged-dependencies.txt");
            if (stream is null) return names;
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                    continue;
                names.Add(Path.GetFileNameWithoutExtension(trimmed));
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"LoadMergedAssemblyNames failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: merged assemblies will resolve from loose files — check SmartCon.App resources]");
        }
        return names;
    }
#endif

    /// <summary>
    /// Last-resort assembly resolution for the add-in folder.
    /// net48: merged third-party names (ADR-051) are served from SmartCon.Dependencies.dll —
    /// never from loose files and never from another add-in's version. Everything else falls
    /// back to already-loaded assemblies, then to the plugin folder.
    /// net8: safety net for the default context only; the isolated AssemblyLoadContext
    /// resolves plugin dependencies on its own.
    /// </summary>
    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        try
        {
            var requested = new AssemblyName(args.Name);
            var name = requested.Name;
            if (string.IsNullOrEmpty(name))
                return null;

            using var _scope = SmartConLogger.BeginScope("AsmResolve",
                ("Method", nameof(OnAssemblyResolve)),
                ("Assembly", name));

#if NETFRAMEWORK
            if (s_mergedAssemblyNames.Value.Contains(name))
            {
                var merged = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "SmartCon.Dependencies");
                if (merged is null)
                {
                    var mergedDir = Path.GetDirectoryName(typeof(App).Assembly.Location);
                    var mergedPath = mergedDir is null ? null : Path.Combine(mergedDir, "SmartCon.Dependencies.dll");
                    if (mergedPath is not null && File.Exists(mergedPath))
                        merged = Assembly.LoadFrom(mergedPath);
                }

                if (merged is null)
                {
                    SmartConLogger.Warn(
                        $"Merged assembly requested but SmartCon.Dependencies.dll not found for '{args.Name}' " +
                        "[Action: reinstall SmartCon via setup.exe — the merged dependency assembly is missing]");
                    return null;
                }

                SmartConLogger.Debug($"Resolved '{args.Name}' from SmartCon.Dependencies (merged)");
                return merged;
            }
#endif

            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == name);
            var pluginDir = Path.GetDirectoryName(typeof(App).Assembly.Location);
            var path = pluginDir is null ? null : Path.Combine(pluginDir, name + ".dll");

            // Another add-in already loaded an OLDER version than requested:
            // returning it would surface as MissingMethod/TypeLoad/0x80131040 downstream
            // (Issue #134). Prefer the file from our folder when it satisfies the request —
            // for strong-named dependencies this yields an exact-identity side-by-side load.
            if (loaded != null && requested.Version is not null
                && loaded.GetName().Version is not null
                && loaded.GetName().Version < requested.Version
                && path is not null && File.Exists(path))
            {
                try
                {
                    var onDisk = AssemblyName.GetAssemblyName(path);
                    if (onDisk.Version is not null && onDisk.Version >= requested.Version)
                    {
                        SmartConLogger.Debug(
                            $"Resolved '{args.Name}' from plugin folder (already-loaded v{loaded.GetName().Version} is older)");
                        return Assembly.LoadFrom(path);
                    }
                }
                catch (Exception ex)
                {
                    SmartConLogger.Debug($"Version probe failed for '{path}': {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (loaded != null)
            {
                var loadedVersion = loaded.GetName().Version;
                if (requested.Version is not null && loadedVersion is not null && loadedVersion < requested.Version)
                {
                    SmartConLogger.Warn(
                        $"Version downgrade: requested '{args.Name}', returning already-loaded " +
                        $"{loaded.FullName} from '{loaded.Location}' " +
                        "[Action: another add-in loaded an older version first — update it; " +
                        "see Issue #134 for the isolation roadmap]");
                }
                else
                {
                    SmartConLogger.Debug($"Resolved '{args.Name}' from already-loaded v{loadedVersion}");
                }
                return loaded;
            }

            if (pluginDir is null)
                return null;
            if (path is not null && File.Exists(path))
            {
                SmartConLogger.Debug($"Resolved '{args.Name}' from plugin folder");
                return Assembly.LoadFrom(path);
            }

            SmartConLogger.Debug($"Not resolved: '{args.Name}'");
            return null;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"OnAssemblyResolve failed for '{args.Name}': {ex.GetType().Name}: {ex.Message} " +
                "[Action: report to SmartCon support with smartcon.log]");
            return null;
        }
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
