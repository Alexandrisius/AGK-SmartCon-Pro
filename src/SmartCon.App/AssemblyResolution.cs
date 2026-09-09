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

public sealed partial class App
{
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
