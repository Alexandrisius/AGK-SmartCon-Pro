using System.IO;
using SmartCon.Core.Logging;

namespace SmartCon.Core.Services.FamilyManager;

/// <summary>
/// One-shot cleanup helper for the legacy <c>files/_stage/</c> folder
/// used by SmartCon &lt; v2.0.0 for transient family staging. The folder
/// has been removed from the runtime flow (see ADR-035) but may still
/// exist on disk for users upgrading from a prior version.
/// </summary>
/// <remarks>
/// Kept in <c>SmartCon.Core</c> (no Revit dependency) so the logic can
/// be exercised by headless unit tests. The production entry point lives
/// in <c>SmartCon.App</c> and runs once during <c>OnStartup</c>.
/// </remarks>
public static class LegacyStageFolderCleaner
{
    /// <summary>
    /// Removes the <c>files/_stage/</c> folder from every catalog directory
    /// under <paramref name="familyManagerRoot"/>. The operation is idempotent
    /// and never throws — exceptions are logged at <c>Debug</c> level so a
    /// permission glitch on one catalog does not block startup.
    /// </summary>
    public static void Cleanup(string familyManagerRoot)
    {
        if (string.IsNullOrEmpty(familyManagerRoot)) return;
        try
        {
            if (!Directory.Exists(familyManagerRoot)) return;

            foreach (var catalogDir in Directory.GetDirectories(familyManagerRoot))
            {
                var stageDir = Path.Combine(catalogDir, "files", "_stage");
                if (!Directory.Exists(stageDir)) continue;

                try
                {
                    Directory.Delete(stageDir, recursive: true);
                    SmartConLogger.Info($"Removed legacy staging folder: {stageDir}");
                }
                catch (Exception ex)
                {
                    SmartConLogger.Debug(
                        $"LegacyStageFolderCleaner: failed to remove '{stageDir}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"LegacyStageFolderCleaner: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
