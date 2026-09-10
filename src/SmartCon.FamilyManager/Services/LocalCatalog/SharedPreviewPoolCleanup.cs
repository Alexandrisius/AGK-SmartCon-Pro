using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

/// <summary>
/// #249 (Phase 5, CAS refcount rules): pooled preview files are shared
/// across versions AND families, so a pooled file is deleted ONLY when
/// the last <c>family_assets</c> row referencing it disappears. All
/// entry points are best-effort and run AFTER the owning DB change
/// committed (a rollback can never resurrect a reference whose file is
/// already gone). A file left behind by a crashed cleanup is a harmless
/// orphan — the pool garbage sweep collects it.
/// </summary>
internal static class SharedPreviewPoolCleanup
{
    /// <summary>
    /// Deletes the pooled file at <paramref name="relativePath"/> when no
    /// <c>family_assets</c> row references it anymore. No-op for
    /// non-pool paths and for files still referenced. Never throws.
    /// </summary>
    public static async Task DeletePoolFileIfOrphanedAsync(
        LocalCatalogDatabase database, string? relativePath, CancellationToken ct)
    {
        if (!StoragePathResolver.IsSharedPreviewPoolPath(relativePath))
        {
            return;
        }

        try
        {
            using var connection = database.CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM family_assets WHERE relative_path = @p";
            cmd.Parameters.Add(new SqliteParameter("@p", relativePath!));
            var references = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            if (references > 0)
            {
                return;
            }

            var absolutePath = Path.Combine(database.GetDatabaseRoot(), relativePath!);
            if (File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
                SmartConLogger.Debug(
                    $"CAS pool: deleted orphaned preview '{Path.GetFileName(absolutePath)}' (last reference removed)");
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"CAS pool: orphan cleanup skipped for '{relativePath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Garbage sweep of the shared pool: deletes every pooled file with
    /// zero referencing <c>family_assets</c> rows (crashed imports,
    /// historical bugs). Returns the number of files deleted.
    /// </summary>
    public static async Task<int> SweepOrphanedPoolFilesAsync(
        LocalCatalogDatabase database, CancellationToken ct)
    {
        var poolDir = Path.Combine(database.GetDatabaseRoot(), "files", "_shared", "models");
        if (!Directory.Exists(poolDir))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var shardDir in Directory.EnumerateDirectories(poolDir))
        {
            foreach (var file in Directory.EnumerateFiles(shardDir, "*.glb"))
            {
                ct.ThrowIfCancellationRequested();
                var relativePath = StoragePathResolver.SharedPreviewPoolRelativePrefix
                    + Path.GetFileName(shardDir) + "/" + Path.GetFileName(file);
                try
                {
                    using var connection = database.CreateConnection();
                    await connection.OpenAsync(ct).ConfigureAwait(false);
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*) FROM family_assets WHERE relative_path = @p";
                    cmd.Parameters.Add(new SqliteParameter("@p", relativePath));
                    var references = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
                    if (references > 0)
                    {
                        continue;
                    }

                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex)
                {
                    SmartConLogger.Debug($"CAS pool sweep: skipped '{Path.GetFileName(file)}': {ex.Message}");
                }
            }
        }

        if (deleted > 0)
        {
            SmartConLogger.Info($"CAS pool sweep: deleted {deleted} orphaned preview file(s)");
        }
        return deleted;
    }
}
