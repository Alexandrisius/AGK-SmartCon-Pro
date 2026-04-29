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

    public string GetCanonicalRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "SmartCon", "FamilyManager");
    }

    public async Task<FamilyResolvedFile> ResolveForLoadAsync(string versionId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT cv.id AS version_id, cv.catalog_item_id, ff.cached_path, ff.original_path, ff.storage_mode
            FROM catalog_versions cv
            INNER JOIN family_files ff ON ff.id = cv.file_id
            WHERE cv.id = @versionId
            """;
        cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new FamilyResolvedFile(
                AbsolutePath: "",
                CatalogItemId: null,
                VersionId: versionId);
        }

        var catalogItemId = reader.GetString(reader.GetOrdinal("catalog_item_id"));
        var cachedPath = reader.IsDBNull(reader.GetOrdinal("cached_path"))
            ? null
            : reader.GetString(reader.GetOrdinal("cached_path"));
        var originalPath = reader.IsDBNull(reader.GetOrdinal("original_path"))
            ? null
            : reader.GetString(reader.GetOrdinal("original_path"));
        var storageMode = (FamilyFileStorageMode)Enum.Parse(typeof(FamilyFileStorageMode), reader.GetString(reader.GetOrdinal("storage_mode")));

        SmartConLogger.Info($"[FileResolver] versionId={versionId}, mode={storageMode}, originalPath={originalPath ?? "null"}, cachedPath={cachedPath ?? "null"}");

        if (storageMode == FamilyFileStorageMode.Missing)
        {
            SmartConLogger.Info($"[FileResolver] StorageMode=Missing → returning empty path");
            return new FamilyResolvedFile(
                AbsolutePath: "",
                CatalogItemId: catalogItemId,
                VersionId: versionId);
        }

        if (storageMode == FamilyFileStorageMode.Linked)
        {
            if (!string.IsNullOrEmpty(originalPath) && File.Exists(originalPath))
            {
                SmartConLogger.Info($"[FileResolver] Linked → resolved: {originalPath}");
                return new FamilyResolvedFile(
                    AbsolutePath: originalPath!,
                    CatalogItemId: catalogItemId,
                    VersionId: versionId);
            }

            SmartConLogger.Info($"[FileResolver] Linked → file NOT found at: {originalPath ?? "null"}");
            return new FamilyResolvedFile(
                AbsolutePath: "",
                CatalogItemId: catalogItemId,
                VersionId: versionId);
        }

        if (string.IsNullOrEmpty(cachedPath))
        {
            SmartConLogger.Info($"[FileResolver] Cached → cachedPath is empty");
            return new FamilyResolvedFile(
                AbsolutePath: "",
                CatalogItemId: catalogItemId,
                VersionId: versionId);
        }

        var absolutePath = Path.Combine(GetCanonicalRoot(), cachedPath);
        SmartConLogger.Info($"[FileResolver] Cached → resolved: {absolutePath}");
        return new FamilyResolvedFile(
            AbsolutePath: absolutePath,
            CatalogItemId: catalogItemId,
            VersionId: versionId);
    }
}
