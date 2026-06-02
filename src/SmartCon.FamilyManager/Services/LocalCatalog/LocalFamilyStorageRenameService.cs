using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class LocalFamilyStorageRenameService : IFamilyStorageRenameService
{
    private readonly LocalCatalogDatabase _database;
    private readonly StoragePathResolver _pathResolver;

    public LocalFamilyStorageRenameService(LocalCatalogDatabase database, StoragePathResolver pathResolver)
    {
        _database = database;
        _pathResolver = pathResolver;
    }

    public async Task RenameFamilyFilesAsync(string catalogItemId, string newName, CancellationToken ct = default)
    {
        var trimmedNewName = newName?.Trim() ?? string.Empty;
        SmartConLogger.Info($"[FM Rename] Starting rename for item {catalogItemId} to '{trimmedNewName}'");

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();

        try
        {
            // 1. Determine current version label
            string? currentVersionLabel;
            using (var versionCmd = connection.CreateCommand())
            {
                versionCmd.CommandText = "SELECT current_version_label FROM catalog_items WHERE id = @id";
                versionCmd.Parameters.Add(new SqliteParameter("@id", catalogItemId));
                var result = await versionCmd.ExecuteScalarAsync(ct);
                currentVersionLabel = result is DBNull or null ? null : (string)result;
            }

            SmartConLogger.Info($"[FM Rename] current_version_label = '{currentVersionLabel}'");

        if (string.IsNullOrEmpty(currentVersionLabel) || string.IsNullOrWhiteSpace(trimmedNewName))
        {
            SmartConLogger.Warn($"[FM Rename] current_version_label is empty or newName is whitespace — aborting");
            return;
        }

            // 2. Find all family_files for the current version
            var filesToRename = new List<(FileRecord Record, string OldAbsolutePath)>();
            using (var selectCmd = connection.CreateCommand())
            {
                selectCmd.CommandText = """
                    SELECT ff.id, ff.relative_path, ff.file_name
                    FROM family_files ff
                    INNER JOIN catalog_versions cv ON cv.file_id = ff.id
                    WHERE cv.catalog_item_id = @itemId
                      AND cv.version_label = @versionLabel
                    """;
                selectCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                selectCmd.Parameters.Add(new SqliteParameter("@versionLabel", currentVersionLabel));

                using var reader = await selectCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var id = reader.GetString(0);
                    var relativePath = reader.GetString(1);
                    var fileName = reader.GetString(2);
                    var absolutePath = Path.Combine(_pathResolver.GetDatabaseRoot(), relativePath);
                    filesToRename.Add((new FileRecord(id, relativePath, fileName), absolutePath));
                    SmartConLogger.Info($"[FM Rename] Found file: id={id}, rel='{relativePath}', abs='{absolutePath}', name='{fileName}'");
                }
            }

            SmartConLogger.Info($"[FM Rename] Found {filesToRename.Count} files to rename");

            // 3. Rename each file on disk and update DB
            foreach (var (record, oldAbsolutePath) in filesToRename)
            {
                var oldExtension = Path.GetExtension(record.FileName);
                var newFileName = trimmedNewName + oldExtension;

                var oldRelativeDir = Path.GetDirectoryName(record.RelativePath) ?? string.Empty;
                var newRelativePath = Path.Combine(oldRelativeDir, newFileName).Replace('/', Path.DirectorySeparatorChar);
                var newAbsolutePath = Path.Combine(_pathResolver.GetDatabaseRoot(), newRelativePath);

                SmartConLogger.Info($"[FM Rename] Processing: old='{oldAbsolutePath}' -> new='{newAbsolutePath}'");

                if (File.Exists(oldAbsolutePath) && !File.Exists(newAbsolutePath))
                {
                    await Task.Run(() =>
                    {
                        var newDir = Path.GetDirectoryName(newAbsolutePath);
                        if (!string.IsNullOrEmpty(newDir) && !Directory.Exists(newDir))
                            Directory.CreateDirectory(newDir);

                        File.Move(oldAbsolutePath, newAbsolutePath);
                    }, ct);
                    SmartConLogger.Info($"[FM Rename] File moved successfully");

                    // Переименовываем Type Catalog (.txt) если есть
                    var oldTxtPath = Path.ChangeExtension(oldAbsolutePath, ".txt");
                    var newTxtPath = Path.ChangeExtension(newAbsolutePath, ".txt");
                    if (File.Exists(oldTxtPath) && !File.Exists(newTxtPath))
                    {
                        await Task.Run(() => File.Move(oldTxtPath, newTxtPath), ct);
                        SmartConLogger.Info($"[FM Rename] Type Catalog moved: {oldTxtPath} -> {newTxtPath}");
                    }
                }
                else
                {
                    SmartConLogger.Warn($"[FM Rename] Skipped: oldExists={File.Exists(oldAbsolutePath)}, newExists={File.Exists(newAbsolutePath)}");
                }

                using var updateCmd = connection.CreateCommand();
                updateCmd.CommandText = """
                    UPDATE family_files
                    SET file_name = @newFileName,
                        relative_path = @newRelativePath
                    WHERE id = @id
                    """;
                updateCmd.Parameters.Add(new SqliteParameter("@newFileName", newFileName));
                updateCmd.Parameters.Add(new SqliteParameter("@newRelativePath", newRelativePath));
                updateCmd.Parameters.Add(new SqliteParameter("@id", record.Id));
                await updateCmd.ExecuteNonQueryAsync(ct);
                SmartConLogger.Info($"[FM Rename] DB updated for file {record.Id}");
            }

            tx.Commit();
            SmartConLogger.Info($"[FM Rename] Transaction committed successfully");
        }
        catch (Exception ex)
        {
            tx.Rollback();
            SmartConLogger.Error($"[FM Rename] FAILED: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
    }

    private sealed record FileRecord(string Id, string RelativePath, string FileName);
}
