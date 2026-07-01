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
    /// (under %APPDATA%\SmartCon\2025\) so that SharpAssimp's
    /// <c>[DllImport("assimp")]</c> can resolve it.
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
    /// </remarks>
    private static void RegisterNativeLibraryResolvers()
    {
#if NET8_0_OR_GREATER
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
#endif
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
}
