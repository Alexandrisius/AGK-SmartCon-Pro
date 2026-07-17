namespace SmartCon.Core.Deployment;

/// <summary>
/// Единый генератор содержимого манифеста <c>SmartCon.addin</c> (ADR-051).
/// Используется AddinManifestHealer (self-healing при старте) и служит эталоном
/// для установщика / Updater'а. Для Revit 2026+ манифест включает
/// <c>ManifestSettings</c> — нативную изоляцию AssemblyLoadContext.
/// На Revit 2025 тег запрещён (парсер отвергает весь файл) — там изоляцию
/// выполняет Nice3point.Revit.Toolkit без участия манифеста.
/// </summary>
public static class SmartConAddinManifest
{
    public const string AddInId = "A1B2C3D4-E5F6-7890-ABCD-EF1234567890";
    public const string IsolationContextName = "SmartCon";
    public const string EntryAssemblyFileName = "SmartCon.App.dll";
    public const string EntryFullClassName = "SmartCon.App.App";

    /// <summary>
    /// Первый год Revit с НАТИВНОЙ поддержкой изоляции через ManifestSettings (Revit 2026+,
    /// "Option for Add-in Dependency Isolation"). На Revit 2025 тег ManifestSettings ЗАПРЕЩЁН:
    /// парсер манифеста отвергает весь файл ("The 'ManifestSettings' tag is incorrect") —
    /// там изоляцию выполняет Nice3point.Revit.Toolkit без участия манифеста (ADR-051).
    /// </summary>
    public const int FirstNativeIsolationRevitYear = 2026;

    public static string Build(string assemblyPath, bool includeIsolationSettings)
    {
        var manifestSettings = includeIsolationSettings
            ? $"""
              <ManifestSettings>
                  <UseRevitContext>False</UseRevitContext>
                  <ContextName>{IsolationContextName}</ContextName>
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
                <AddInId>{AddInId}</AddInId>
                <FullClassName>{EntryFullClassName}</FullClassName>
                <VendorId>AGK</VendorId>
                <VendorDescription>AGK Engineering</VendorDescription>
              </AddIn>
            {manifestSettings}</RevitAddIns>
            """;
    }

    /// <summary>Подпапка установки (%APPDATA%\SmartCon\{subfolder}) для года Revit.</summary>
    public static string GetInstallSubfolder(int revitYear) => revitYear switch
    {
        <= 2020 => "2019-2020",
        >= 2021 and <= 2023 => "2021-2023",
        2024 => "2024",
        2025 => "2025",
        _ => "2026"
    };

    /// <summary>Годы Revit, чьи манифесты указывают на данную подпапку установки.</summary>
    public static IReadOnlyList<int> GetManifestYearsForSubfolder(string subfolder) => subfolder switch
    {
        "2019-2020" => new[] { 2019, 2020 },
        "2021-2023" => new[] { 2021, 2022, 2023 },
        "2024" => new[] { 2024 },
        "2025" => new[] { 2025 },
        "2026" => new[] { 2026 },
        _ => Array.Empty<int>()
    };
}
