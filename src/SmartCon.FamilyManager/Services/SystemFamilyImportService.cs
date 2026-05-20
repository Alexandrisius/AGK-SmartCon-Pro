using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services;

internal sealed class SystemFamilyImportService : ISystemFamilyImportService
{
    private readonly ISystemFamilyRevitOperations _revitOps;
    private readonly LocalCatalog.LocalCatalogDatabase _database;
    private readonly LocalCatalog.LocalCatalogMigrator _migrator;
    private readonly LocalCatalog.StoragePathResolver _pathResolver;
    private readonly IRevitContext _revitContext;

    public SystemFamilyImportService(
        ISystemFamilyRevitOperations revitOps,
        LocalCatalog.LocalCatalogDatabase database,
        LocalCatalog.LocalCatalogMigrator migrator,
        LocalCatalog.StoragePathResolver pathResolver,
        IRevitContext revitContext)
    {
        _revitOps = revitOps;
        _database = database;
        _migrator = migrator;
        _pathResolver = pathResolver;
        _revitContext = revitContext;
    }

    public SystemFamilyImportResult ImportFromSelection()
    {
        var pipeTypes = _revitOps.PickPipeTypes();
        if (pipeTypes.Count == 0)
            return new SystemFamilyImportResult(false, "No pipe types selected", null, 0);

        SmartConLogger.Freeze($"[SystemFamilyImport] Selected {pipeTypes.Count} unique PipeTypes");

        var uniqueIds = pipeTypes.Select(p => p.UniqueId).ToList();
        var createResult = _revitOps.CreateCleanProjectWithTypes(uniqueIds);

        if (!createResult.Success || string.IsNullOrEmpty(createResult.FilePath))
            return new SystemFamilyImportResult(false, createResult.Error ?? "Failed to create project", null, 0);

        SmartConLogger.Freeze($"[SystemFamilyImport] Clean .rvt created: {createResult.CopiedElementsCount} elements");

        try
        {
            var result = SaveToStorageAsync(createResult.FilePath!, pipeTypes).GetAwaiter().GetResult();
            return result;
        }
        finally
        {
            try { File.Delete(createResult.FilePath); } catch { }
        }
    }

    private async Task<SystemFamilyImportResult> SaveToStorageAsync(string rvtPath, IReadOnlyList<SelectedPipeType> pipeTypes)
    {
        await _migrator.MigrateAsync();

        var sha256 = ComputeSha256(rvtPath);
        var fileSize = new FileInfo(rvtPath).Length;
        var revitVersion = int.Parse(_revitContext.GetRevitVersion());
        var familyName = $"Трубы {DateTime.Now:yyyy-MM-dd_HH-mm}";
        var normalizedName = FamilyNameNormalizer.Normalize(familyName);
        var now = DateTimeOffset.UtcNow;

        var catalogItemId = Guid.NewGuid().ToString();
        var versionId = Guid.NewGuid().ToString();
        var fileRecordId = Guid.NewGuid().ToString();
        var versionLabel = "v1";
        var fileName = $"{normalizedName.Replace(' ', '_')}.rvt";

        _pathResolver.EnsureFamilyDirectories(catalogItemId, versionLabel, revitVersion);
        var destPath = _pathResolver.GetRfaFilePath(catalogItemId, versionLabel, revitVersion, fileName);
        File.Copy(rvtPath, destPath, true);
        File.SetAttributes(destPath, File.GetAttributes(destPath) | FileAttributes.ReadOnly);

        var relativePath = _pathResolver.GetRelativePath(destPath);

        using var connection = _database.CreateConnection();
        await connection.OpenAsync();

        using var fkCmd = connection.CreateCommand();
        fkCmd.CommandText = "PRAGMA foreign_keys=ON;";
        await fkCmd.ExecuteNonQueryAsync();

        using var tx = connection.BeginTransaction();
        try
        {
            using (var fileCmd = connection.CreateCommand())
            {
                fileCmd.CommandText = """
                    INSERT INTO family_files (id, relative_path, file_name, size_bytes, sha256, revit_major_version, imported_at_utc)
                    VALUES (@id, @relPath, @fileName, @size, @sha256, @revit, @importedAt)
                    """;
                fileCmd.Parameters.Add(new SqliteParameter("@id", fileRecordId));
                fileCmd.Parameters.Add(new SqliteParameter("@relPath", relativePath));
                fileCmd.Parameters.Add(new SqliteParameter("@fileName", fileName));
                fileCmd.Parameters.Add(new SqliteParameter("@size", fileSize));
                fileCmd.Parameters.Add(new SqliteParameter("@sha256", sha256));
                fileCmd.Parameters.Add(new SqliteParameter("@revit", revitVersion));
                fileCmd.Parameters.Add(new SqliteParameter("@importedAt", now.ToString("o")));
                await fileCmd.ExecuteNonQueryAsync();
            }

            using (var itemCmd = connection.CreateCommand())
            {
                itemCmd.CommandText = """
                    INSERT INTO catalog_items (id, name, normalized_name, description, category_name, category_id, manufacturer, content_status, current_version_label, published_by, created_at_utc, updated_at_utc, family_source, revit_category)
                    VALUES (@id, @name, @normName, @desc, @catName, @catId, @mfr, @status, @verLabel, @pubBy, @createdAt, @updatedAt, 'system', 'OST_PipeCurves')
                    """;
                itemCmd.Parameters.Add(new SqliteParameter("@id", catalogItemId));
                itemCmd.Parameters.Add(new SqliteParameter("@name", familyName));
                itemCmd.Parameters.Add(new SqliteParameter("@normName", normalizedName));
                itemCmd.Parameters.Add(new SqliteParameter("@desc", DBNull.Value));
                itemCmd.Parameters.Add(new SqliteParameter("@catName", DBNull.Value));
                itemCmd.Parameters.Add(new SqliteParameter("@catId", DBNull.Value));
                itemCmd.Parameters.Add(new SqliteParameter("@mfr", DBNull.Value));
                itemCmd.Parameters.Add(new SqliteParameter("@status", "Active"));
                itemCmd.Parameters.Add(new SqliteParameter("@verLabel", versionLabel));
                itemCmd.Parameters.Add(new SqliteParameter("@pubBy", _revitContext.GetUsername()));
                itemCmd.Parameters.Add(new SqliteParameter("@createdAt", now.ToString("o")));
                itemCmd.Parameters.Add(new SqliteParameter("@updatedAt", now.ToString("o")));
                await itemCmd.ExecuteNonQueryAsync();
            }

            using (var verCmd = connection.CreateCommand())
            {
                verCmd.CommandText = """
                    INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label, sha256, revit_major_version, types_count, parameters_count, published_at_utc)
                    VALUES (@id, @itemId, @fileId, @verLabel, @sha256, @revit, @typesCount, 0, @pubAt)
                    """;
                verCmd.Parameters.Add(new SqliteParameter("@id", versionId));
                verCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                verCmd.Parameters.Add(new SqliteParameter("@fileId", fileRecordId));
                verCmd.Parameters.Add(new SqliteParameter("@verLabel", versionLabel));
                verCmd.Parameters.Add(new SqliteParameter("@sha256", sha256));
                verCmd.Parameters.Add(new SqliteParameter("@revit", revitVersion));
                verCmd.Parameters.Add(new SqliteParameter("@typesCount", pipeTypes.Count));
                verCmd.Parameters.Add(new SqliteParameter("@pubAt", now.ToString("o")));
                await verCmd.ExecuteNonQueryAsync();
            }

            var sortOrder = 0;
            foreach (var pt in pipeTypes)
            {
                using var typeCmd = connection.CreateCommand();
                typeCmd.CommandText = """
                    INSERT OR IGNORE INTO family_types (id, catalog_item_id, type_name, sort_order, type_unique_id)
                    VALUES (@id, @itemId, @typeName, @sort, @uniqueId)
                    """;
                typeCmd.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString()));
                typeCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                typeCmd.Parameters.Add(new SqliteParameter("@typeName", pt.Name));
                typeCmd.Parameters.Add(new SqliteParameter("@sort", sortOrder));
                typeCmd.Parameters.Add(new SqliteParameter("@uniqueId", pt.UniqueId));
                await typeCmd.ExecuteNonQueryAsync();
                sortOrder++;
            }

            tx.Commit();
        }
        catch (Exception ex)
        {
            SmartConLogger.Freeze($"[SystemFamilyImport] DB error: {ex.Message}\n{ex.StackTrace}");
            tx.Rollback();
            throw;
        }

        _database.Checkpoint();

        SmartConLogger.Freeze($"[SystemFamilyImport] Saved: {familyName}, {pipeTypes.Count} types, {fileSize / 1024} KB");

        return new SystemFamilyImportResult(true, $"Imported {pipeTypes.Count} pipe types", catalogItemId, pipeTypes.Count);
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "");
    }
}
