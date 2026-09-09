using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed partial class DatabaseManager
{
    public async Task<bool> DeleteDatabaseAsync(string connectionId, CancellationToken ct = default)
    {
        await _registryLock.WaitAsync(ct);
        try
        {
            var registry = await LoadRegistryAsync(ct);
            var conn = registry.Connections.FirstOrDefault(c => c.Id == connectionId);
            if (conn is null)
                return false;

            var connections = registry.Connections.Where(c => c.Id != connectionId).ToList();

            string? newActiveId = registry.ActiveConnectionId;
            if (registry.ActiveConnectionId == connectionId)
            {
                var other = connections.FirstOrDefault();
                newActiveId = other?.Id;
                if (other is not null)
                    _catalogDatabase.SwitchToPath(other.Path);
            }

            if (Directory.Exists(conn.Path))
            {
                // Release any idle SQLite handles before touching the filesystem.
                SqliteConnection.ClearAllPools();

                try
                {
                    var deleted = await SafeDeleteDirectoryAsync(conn.Path, _trashPath, ct);
                    if (!deleted)
                    {
                        using var _scope = SmartConLogger.BeginScope("DatabaseManager",
                            ("Method", nameof(DeleteDatabaseAsync)),
                            ("DatabaseName", conn.Name),
                            ("FileName", Path.GetFileName(conn.Path)));
                        SmartConLogger.Warn($"Database files for '{conn.Name}' moved to trash because they were locked. " +
                            $"[Action: remaining files will be removed on the next Revit launch]");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    using var _scope = SmartConLogger.BeginScope("DatabaseManager",
                        ("Method", nameof(DeleteDatabaseAsync)),
                        ("DatabaseName", conn.Name),
                        ("FileName", Path.GetFileName(conn.Path)),
                        ("Exception", ex.GetType().Name));
                    SmartConLogger.Warn($"Could not delete database files for '{conn.Name}' because they are in use. " +
                        $"[Action: close Revit and remove remaining folder manually: {conn.Path}]");

                    await SaveRegistryAsync(new DatabaseConnectionRegistry(newActiveId, connections), ct);

                    if (newActiveId != registry.ActiveConnectionId)
                    {
                        ActiveDatabaseChanged?.Invoke(this, newActiveId);
                    }

                    var message = LanguageManager.GetString(StringLocalization.Keys.FM_DbDeleteFilesLocked)
                        ?? "Database \"{0}\" removed from the list, but files could not be deleted because they are in use. Close Revit to remove remaining files.";
                    throw new InvalidOperationException(string.Format(message, conn.Name), ex);
                }
            }

            await SaveRegistryAsync(new DatabaseConnectionRegistry(newActiveId, connections), ct);

            if (newActiveId != registry.ActiveConnectionId)
            {
                ActiveDatabaseChanged?.Invoke(this, newActiveId);
            }

            SmartConLogger.Info($"DatabaseManager.Delete: Database '{conn.Name}' deleted");
            return true;
        }
        finally
        {
            _registryLock.Release();
        }
    }

    private void EnsureDatabaseWritable(string dbFile)
    {
        if (!File.Exists(dbFile))
            return;

        try
        {
            using var connection = _catalogDatabase.CreateConnectionForPath(dbFile);
            connection.Open();
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = user_version";
            cmd.ExecuteNonQuery();
            tx.Rollback();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 8)
        {
            throw new InvalidOperationException(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbReadOnlyError)
                ?? "Database is read-only. Change file permissions or contact your administrator.");
        }
    }

    private static async Task<bool> SafeDeleteDirectoryAsync(string path, string trashPath, CancellationToken ct, int maxRetries = 5)
    {
        if (!Directory.Exists(path))
            return true;

        ClearDirectoryAttributes(path);

        for (var i = 0; i < maxRetries; i++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (i < maxRetries - 1)
                {
                    await Task.Delay(200 * (i + 1), ct);
                    ClearDirectoryAttributes(path);
                }
            }
        }

        // Fallback: move the locked folder to the trash area so it can be retried later.
        Directory.CreateDirectory(trashPath);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var guidSuffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var trashName = $"{Path.GetFileName(path)}_{timestamp}_{guidSuffix}";
        var trashItemPath = Path.Combine(trashPath, trashName);

        Directory.Move(path, trashItemPath);
        return false;
    }

    private static void ClearDirectoryAttributes(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        catch
        {
            // ignored
        }

        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch
            {
                // ignored
            }
        }

        foreach (var dir in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(dir, FileAttributes.Normal);
            }
            catch
            {
                // ignored
            }
        }
    }

    private async Task CleanupTrashAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_trashPath))
            return;

        var dirs = await Task.Run(() => Directory.GetDirectories(_trashPath), ct);
        foreach (var dir in dirs)
        {
            try
            {
                ClearDirectoryAttributes(dir);
                await Task.Run(() => Directory.Delete(dir, recursive: true), ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                using var _scope = SmartConLogger.BeginScope("DatabaseManager",
                    ("Method", nameof(CleanupTrashAsync)),
                    ("FileName", Path.GetFileName(dir)));
                SmartConLogger.Warn($"Could not clean up trashed folder '{dir}'. It will be retried on the next launch. " +
                    $"[Action: close Revit if the folder is still locked]");
            }
        }
    }
}
