using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed partial class LocalCatalogProvider
{
    public async Task<FamilyCatalogItem> UpdateItemAsync(string id, string? name, string? description, string? categoryId, IReadOnlyList<string>? tags, ContentStatus? status, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var tx = connection.BeginTransaction();

        try
        {
            var setClauses = new List<string>();
            var cmd = connection.CreateCommand();

            if (name is not null)
            {
                setClauses.Add("name = @name");
                setClauses.Add("normalized_name = @normalizedName");
                cmd.Parameters.Add(new SqliteParameter("@name", name));
                cmd.Parameters.Add(new SqliteParameter("@normalizedName", Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(name)));
            }

            if (description is not null)
            {
                setClauses.Add("description = @description");
                cmd.Parameters.Add(new SqliteParameter("@description", description));
            }

            setClauses.Add("category_id = @categoryId");
            cmd.Parameters.Add(new SqliteParameter("@categoryId", (object?)categoryId ?? DBNull.Value));

            if (status is not null)
            {
                setClauses.Add("content_status = @status");
                cmd.Parameters.Add(new SqliteParameter("@status", status.Value.ToString()));
            }

            setClauses.Add("updated_at_utc = @updatedAtUtc");
            cmd.Parameters.Add(new SqliteParameter("@updatedAtUtc", DateTimeOffset.UtcNow.ToString("o")));
            cmd.Parameters.Add(new SqliteParameter("@id", id));

            cmd.CommandText = $"UPDATE catalog_items SET {string.Join(", ", setClauses)} WHERE id = @id";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            if (tags is not null)
            {
                using var delCmd = connection.CreateCommand();
                delCmd.CommandText = "DELETE FROM catalog_tags WHERE catalog_item_id = @id";
                delCmd.Parameters.Add(new SqliteParameter("@id", id));
                await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                foreach (var tag in tags)
                {
                    var normalizedTag = Core.Services.FamilyManager.FamilySearchNormalizer.Normalize(tag);
                    using var tagCmd = connection.CreateCommand();
                    tagCmd.CommandText = "INSERT OR IGNORE INTO catalog_tags (catalog_item_id, tag, normalized_tag) VALUES (@id, @tag, @normalizedTag)";
                    tagCmd.Parameters.Add(new SqliteParameter("@id", id));
                    tagCmd.Parameters.Add(new SqliteParameter("@tag", tag));
                    tagCmd.Parameters.Add(new SqliteParameter("@normalizedTag", normalizedTag));
                    await tagCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        return (await GetItemAsync(id, ct).ConfigureAwait(false))!;
    }

    public async Task<bool> DeleteItemAsync(string id, CancellationToken ct = default)
    {
        var dbRoot = _database.GetDatabaseRoot();
        var familyDir = Path.Combine(dbRoot, "files", id);
        var dirExists = Directory.Exists(familyDir);

        // Attempt file deletion BEFORE database transaction.
        // If files are locked, exception surfaces here and DB record remains intact.
        if (dirExists)
        {
            await DeleteDirectoryWithRetryAsync(familyDir, ct).ConfigureAwait(false);
        }

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA foreign_keys = ON";
            await pragmaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // #249 (Phase 5): capture the item's CAS pool paths BEFORE the
        // CASCADE delete — each pooled file is deleted only when no other
        // catalog item references it anymore (refcount, Plan v3).
        var capturedPoolPaths = new List<string>();
        using (var poolCmd = connection.CreateCommand())
        {
            poolCmd.CommandText = """
                SELECT relative_path FROM family_assets
                WHERE catalog_item_id = @id AND relative_path LIKE @poolPrefix
                """;
            poolCmd.Parameters.Add(new SqliteParameter("@id", id));
            poolCmd.Parameters.Add(new SqliteParameter("@poolPrefix",
                StoragePathResolver.SharedPreviewPoolRelativePrefix + "%"));
            using var poolReader = await poolCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await poolReader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!poolReader.IsDBNull(0))
                {
                    capturedPoolPaths.Add(poolReader.GetString(0));
                }
            }
        }

        int rowsAffected;
        using var tx = connection.BeginTransaction();
        try
        {
            using var delItem = connection.CreateCommand();
            delItem.CommandText = "DELETE FROM catalog_items WHERE id = @id";
            delItem.Parameters.Add(new SqliteParameter("@id", id));
            rowsAffected = await delItem.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        // #249 (Phase 5): refcount cleanup of the captured pool paths —
        // after the commit, never on the rollback path.
        foreach (var poolPath in capturedPoolPaths)
        {
            await SharedPreviewPoolCleanup.DeletePoolFileIfOrphanedAsync(_database, poolPath, ct)
                .ConfigureAwait(false);
        }

        return rowsAffected > 0;
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path, CancellationToken ct, int maxRetries = 5)
    {
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    await Task.Run(() =>
                    {
                        RemoveReadOnlyAttributes(path);
                        Directory.Delete(path, recursive: true);
                    }, ct);
                }
                return;
            }
            catch (IOException ex) when (i < maxRetries - 1)
            {
                using var _scope = SmartConLogger.BeginScope("FM Delete", ("Path", path), ("Attempt", i + 1));
                SmartConLogger.Warn($"failed to delete directory: {ex.Message}. Retrying... [Action: обычно файл заблокирован антивирусом или другим процессом; операция будет повторена до 5 раз]");
                await Task.Delay(200 * (i + 1), ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex) when (i < maxRetries - 1)
            {
                using var _scope = SmartConLogger.BeginScope("FM Delete", ("Path", path), ("Attempt", i + 1));
                SmartConLogger.Warn($"failed (access denied): {ex.Message}. Retrying... [Action: обычно файл заблокирован антивирусом или другим процессом; операция будет повторена до 5 раз]");
                await Task.Delay(200 * (i + 1), ct).ConfigureAwait(false);
            }
        }

        // Final attempt: force GC to release any lingering WPF image handles before last try
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        try
        {
            if (Directory.Exists(path))
            {
                await Task.Run(() =>
                {
                    RemoveReadOnlyAttributes(path);
                    Directory.Delete(path, recursive: true);
                }, ct);
            }
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to delete family directory after {maxRetries} attempts: {path}. The file may be open in Revit or another application. {ex.Message}");
        }
    }

    private static void RemoveReadOnlyAttributes(string path)
    {
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            var attr = File.GetAttributes(file);
            if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
            {
                File.SetAttributes(file, attr & ~FileAttributes.ReadOnly);
            }
        }
    }
}
