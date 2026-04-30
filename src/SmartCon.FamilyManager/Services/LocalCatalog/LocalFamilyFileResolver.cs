using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class LocalFamilyFileResolver : IFamilyFileResolver
{
    private readonly LocalCatalogDatabase _database;

    public LocalFamilyFileResolver(LocalCatalogDatabase database)
    {
        _database = database;
    }

    public string? GetDatabaseRoot()
    {
        return _database.GetDatabaseRoot();
    }

    public async Task<FamilyResolvedFile> ResolveForLoadAsync(string catalogItemId, int targetRevitVersion, CancellationToken ct = default)
    {
        var dbRoot = _database.GetDatabaseRoot();
        if (string.IsNullOrEmpty(dbRoot))
        {
            return new FamilyResolvedFile("", catalogItemId, null);
        }

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ff.relative_path, cv.id AS version_id
            FROM catalog_items ci
            INNER JOIN catalog_versions cv ON cv.catalog_item_id = ci.id AND cv.version_label = ci.current_version_label
            INNER JOIN family_files ff ON ff.id = cv.file_id
            WHERE ci.id = @itemId
            ORDER BY ABS(cv.revit_major_version - @targetRevit) ASC, cv.revit_major_version DESC
            LIMIT 1
            """;
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        cmd.Parameters.Add(new SqliteParameter("@targetRevit", targetRevitVersion));

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            SmartConLogger.Info($"[FileResolver] No version found for item={catalogItemId}, targetRevit={targetRevitVersion}");
            return new FamilyResolvedFile("", catalogItemId, null);
        }

        var relativePath = reader.GetString(0);
        var versionId = reader.GetString(1);
        var absolutePath = Path.Combine(dbRoot, relativePath);

        if (!File.Exists(absolutePath))
        {
            SmartConLogger.Info($"[FileResolver] File not found: {absolutePath}");
            return new FamilyResolvedFile("", catalogItemId, versionId);
        }

        SmartConLogger.Info($"[FileResolver] Resolved: {absolutePath}");
        return new FamilyResolvedFile(absolutePath, catalogItemId, versionId);
    }
}
