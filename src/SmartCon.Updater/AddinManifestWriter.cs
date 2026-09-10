namespace SmartCon.Updater;

/// <summary>
/// Writes SmartCon.addin manifests after an update (ADR-051). The Updater is the only
/// component that can refresh manifests for Revit versions whose plugin updates through
/// the in-app Updater; afterwards the plugin's AddinManifestHealer keeps them current.
/// ManifestSettings (ALC isolation) is included for Revit 2026+ only — on Revit 2025
/// the tag makes Revit reject the whole manifest.
/// NOTE: the manifest template intentionally duplicates SmartCon.Core SmartConAddinManifest —
/// the Updater is self-contained and must not load SmartCon.Core.dll (which references RevitAPI).
/// Keep the XML shape in sync.
/// </summary>
internal static class AddinManifestWriter
{
    public static IReadOnlyList<int> GetYearsForTargetFolder(string folderName) => folderName switch
    {
        "2019-2020" => new[] { 2019, 2020 },
        "2021-2023" => new[] { 2021, 2022, 2023 },
        "2024" => new[] { 2024 },
        "2025" => new[] { 2025 },
        "2026" => new[] { 2026 },
        "2027" => new[] { 2027 },
        _ => Array.Empty<int>()
    };

    public static int WriteManifests(string appDataDir, string targetFolderName, Action<string> log)
    {
        var years = GetYearsForTargetFolder(targetFolderName);
        var written = 0;

        foreach (var year in years)
        {
            try
            {
                var assemblyPath = Path.Combine(appDataDir, "SmartCon", targetFolderName, "SmartCon.App.dll");
                // ManifestSettings — только для Revit 2026+ (нативная изоляция ADR-051);
                // на Revit 2025 тег отвергает весь манифест, изоляцию там делает toolkit.
                var content = BuildManifest(assemblyPath, includeIsolation: year >= 2026);
                var manifestDir = Path.Combine(appDataDir, "Autodesk", "Revit", "Addins", year.ToString());
                Directory.CreateDirectory(manifestDir);
                var manifestPath = Path.Combine(manifestDir, "SmartCon.addin");
                File.WriteAllText(manifestPath, content);
                written++;
                log($"  MANIFEST: wrote SmartCon.addin for Revit {year} (nativeIsolation={(year >= 2026 ? "on" : "n/a")})");
            }
            catch (Exception ex)
            {
                log($"  MANIFEST FAIL: Revit {year} - {ex.Message}");
            }
        }

        return written;
    }

    private static string BuildManifest(string assemblyPath, bool includeIsolation)
    {
        var manifestSettings = includeIsolation
            ? """
              <ManifestSettings>
                  <UseRevitContext>False</UseRevitContext>
                  <ContextName>SmartCon</ContextName>
                </ManifestSettings>

              """
            : string.Empty;

        return
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <RevitAddIns>
              <AddIn Type="Application">
                <Name>SmartCon</Name>
                <Assembly>{assemblyPath}</Assembly>
                <AddInId>A1B2C3D4-E5F6-7890-ABCD-EF1234567890</AddInId>
                <FullClassName>SmartCon.App.App</FullClassName>
                <VendorId>AGK</VendorId>
                <VendorDescription>AGK Engineering</VendorDescription>
              </AddIn>
            {manifestSettings}</RevitAddIns>
            """;
    }
}
