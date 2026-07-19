using SmartCon.Core.Deployment;
using SmartCon.Core.Logging;

namespace SmartCon.App.Diagnostics;

/// <summary>
/// Startup scan of assemblies already loaded into the process, looking for
/// dependency conflicts with the versions SmartCon is built against (ADR-051, Issue #134).
/// Post-isolation (ILRepack on net48, AssemblyLoadContext on net8) a conflict cannot
/// break SmartCon itself — the report is diagnostic and identifies which other add-in
/// forced an older version into the process.
/// </summary>
internal static class DependencyGuard
{
    public static void ScanLoadedAssemblies()
    {
        try
        {
            using var _scope = SmartConLogger.BeginScope("DepGuard",
                ("Method", nameof(ScanLoadedAssemblies)));

            var loaded = new List<LoadedDependencyInfo>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                Version? version;
                string location;
                try
                {
                    var assemblyName = assembly.GetName();
                    name = assemblyName.Name ?? string.Empty;
                    version = assemblyName.Version;
                    location = assembly.IsDynamic ? "(dynamic)" : assembly.Location;
                }
                catch
                {
                    continue;
                }

                if (name.Length == 0 || version is null)
                    continue;
                loaded.Add(new LoadedDependencyInfo(name, version, location));
            }

            var conflicts = DependencyConflictAnalyzer.Analyze(loaded);
            if (conflicts.Count == 0)
            {
                SmartConLogger.Info($"Dependency scan: {loaded.Count} assemblies checked, no conflicts.");
                return;
            }

            foreach (var conflict in conflicts)
            {
                SmartConLogger.Warn(
                    $"Dependency conflict: {conflict.Name} {conflict.LoadedVersion} already loaded " +
                    $"(SmartCon requires >= {conflict.MinimumVersion}) from '{conflict.Location}' " +
                    "[Action: identify the add-in at that path and update it; SmartCon is isolated " +
                    "from this conflict, but the other add-in may fail instead]");
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"DependencyGuard scan failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: non-critical — SmartCon continues to load]");
        }
    }
}
