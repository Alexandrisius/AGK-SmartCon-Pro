using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class LocalFamilyAssetService : IFamilyAssetService
{
    private readonly LocalCatalogDatabase _database;
    private readonly StoragePathResolver _pathResolver;
    private readonly ILocalCatalogMigrator _migrator;
    private string? _migratedDbPath;

    public LocalFamilyAssetService(LocalCatalogDatabase database, StoragePathResolver pathResolver, ILocalCatalogMigrator migrator)
    {
        _database = database;
        _pathResolver = pathResolver;
        _migrator = migrator;
    }

    private async Task EnsureMigratedAsync(CancellationToken ct)
    {
        var currentPath = _database.GetDatabaseRoot();
        if (string.Equals(_migratedDbPath, currentPath, StringComparison.OrdinalIgnoreCase)) return;
        await _migrator.MigrateAsync(ct);
        _migratedDbPath = currentPath;
    }

    public async Task<FamilyAsset> AddAssetAsync(string catalogItemId, string? versionLabel, FamilyAssetType assetType, string sourceFilePath, string? description, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException($"Source file not found: {sourceFilePath}");

        var id = Guid.NewGuid().ToString();
        var fileName = Path.GetFileName(sourceFilePath);
        var fileInfo = new FileInfo(sourceFilePath);
        var assetFolder = StoragePathResolver.GetAssetTypeFolder(assetType);

        var effectiveVersionLabel = versionLabel ?? "shared";

        _pathResolver.EnsureAssetDirectory(catalogItemId, effectiveVersionLabel, assetFolder);
        var destDir = _pathResolver.GetAssetTypeDirectory(catalogItemId, effectiveVersionLabel, assetFolder);
        var destPath = Path.Combine(destDir, fileName);

        var counter = 1;
        while (File.Exists(destPath))
        {
            var nameWithoutExt = SafeFileName.GetBaseName(fileName);
            var ext = Path.GetExtension(fileName);
            destPath = Path.Combine(destDir, $"{nameWithoutExt}_{counter}{ext}");
            counter++;
        }

        await Task.Run(() => File.Copy(sourceFilePath, destPath), ct);

        var relativePath = _pathResolver.GetRelativePath(destPath);
        var now = DateTimeOffset.UtcNow;

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO family_assets (id, catalog_item_id, version_label, asset_type, file_name, relative_path, size_bytes, description, created_at_utc, is_primary)
            VALUES (@id, @catalogItemId, @versionLabel, @assetType, @fileName, @relativePath, @sizeBytes, @description, @createdAtUtc, 0)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@catalogItemId", catalogItemId));
        cmd.Parameters.Add(new SqliteParameter("@versionLabel", (object?)versionLabel ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@assetType", assetType.ToString()));
        cmd.Parameters.Add(new SqliteParameter("@fileName", Path.GetFileName(destPath)));
        cmd.Parameters.Add(new SqliteParameter("@relativePath", relativePath));
        cmd.Parameters.Add(new SqliteParameter("@sizeBytes", fileInfo.Length));
        cmd.Parameters.Add(new SqliteParameter("@description", (object?)description ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@createdAtUtc", now.ToString("o")));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return new FamilyAsset(id, catalogItemId, versionLabel, assetType, Path.GetFileName(destPath), relativePath, fileInfo.Length, description, now, false);
    }

    public async Task<IReadOnlyList<FamilyAsset>> GetAssetsAsync(string catalogItemId, string? versionLabel = null, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();

        if (versionLabel is not null)
        {
            cmd.CommandText = "SELECT * FROM family_assets WHERE catalog_item_id = @itemId AND (version_label = @versionLabel OR version_label IS NULL) ORDER BY created_at_utc DESC";
            cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
            cmd.Parameters.Add(new SqliteParameter("@versionLabel", versionLabel));
        }
        else
        {
            cmd.CommandText = "SELECT * FROM family_assets WHERE catalog_item_id = @itemId ORDER BY created_at_utc DESC";
            cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        }

        var assets = new List<FamilyAsset>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct))
        {
            assets.Add(ReadAsset(reader));
        }

        return assets;
    }

    public async Task<bool> DeleteAssetAsync(string assetId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        string? relativePath;
        string? catalogItemId;
        string? assetTypeStr;
        long isPrimary;
        using (var selectCmd = connection.CreateCommand())
        {
            selectCmd.CommandText = "SELECT relative_path, catalog_item_id, asset_type, is_primary FROM family_assets WHERE id = @id";
            selectCmd.Parameters.Add(new SqliteParameter("@id", assetId));
            using var reader = await selectCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return false;
            relativePath = reader.IsDBNull(0) ? null : reader.GetString(0);
            catalogItemId = reader.IsDBNull(1) ? null : reader.GetString(1);
            assetTypeStr = reader.IsDBNull(2) ? null : reader.GetString(2);
            isPrimary = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
        }

        if (relativePath is null)
            return false;

        using (var deleteCmd = connection.CreateCommand())
        {
            deleteCmd.CommandText = "DELETE FROM family_assets WHERE id = @id";
            deleteCmd.Parameters.Add(new SqliteParameter("@id", assetId));
            var rows = await deleteCmd.ExecuteNonQueryAsync(ct);

            if (rows > 0)
            {
                var absolutePath = Path.Combine(_database.GetDatabaseRoot(), relativePath);
                try
                {
                    await Task.Run(() =>
                    {
                        if (File.Exists(absolutePath))
                            File.Delete(absolutePath);
                    }, ct);
                }
                catch
                {
                }

                // ADR-047 rev 5 / #131: the derived avatar.png was rendered from the
                // primary image — deleting that source must reset the derived avatar,
                // otherwise it survives as an orphan of a deleted original.
                if (isPrimary == 1
                    && string.Equals(assetTypeStr, "Image", StringComparison.Ordinal)
                    && catalogItemId is not null)
                {
                    DeleteAvatarFile(catalogItemId);
                }
            }

            return rows > 0;
        }
    }

    public async Task<string?> ResolveAssetPathAsync(string assetId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT relative_path FROM family_assets WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", assetId));

        var relativePath = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (relativePath is null)
            return null;

        var absolutePath = Path.Combine(_database.GetDatabaseRoot(), relativePath);
        return File.Exists(absolutePath) ? absolutePath : null;
    }

    public async Task SetPrimaryAssetAsync(string assetId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        string? catalogItemId;
        using (var readCmd = connection.CreateCommand())
        {
            readCmd.CommandText = "SELECT catalog_item_id FROM family_assets WHERE id = @id";
            readCmd.Parameters.Add(new SqliteParameter("@id", assetId));
            catalogItemId = await readCmd.ExecuteScalarAsync(ct) as string;
        }

        if (catalogItemId is null) return;

        using var tx = connection.BeginTransaction();
        try
        {
            using (var clearCmd = connection.CreateCommand())
            {
                clearCmd.CommandText = "UPDATE family_assets SET is_primary = 0 WHERE catalog_item_id = @itemId AND asset_type = 'Image'";
                clearCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                await clearCmd.ExecuteNonQueryAsync(ct);
            }

            using (var setCmd = connection.CreateCommand())
            {
                setCmd.CommandText = "UPDATE family_assets SET is_primary = 1 WHERE id = @id";
                setCmd.Parameters.Add(new SqliteParameter("@id", assetId));
                await setCmd.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        // ADR-047 / #131: the derived avatar.png is rendered from the previous
        // primary image — it is stale now and must not survive the primary switch.
        DeleteAvatarFile(catalogItemId);
    }

    public async Task SaveAvatarAsync(string catalogItemId, string sourcePngPath, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        if (!File.Exists(sourcePngPath))
            throw new FileNotFoundException($"Source avatar file not found: {sourcePngPath}");

        var avatarPath = _pathResolver.GetAvatarPath(catalogItemId);
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(avatarPath)!);
            File.Copy(sourcePngPath, avatarPath, overwrite: true);
        }, ct);
    }

    public async Task<string?> GetAvatarImagePathAsync(string catalogItemId, string? versionLabel = null, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);

        var avatarPath = _pathResolver.GetAvatarPath(catalogItemId);
        if (File.Exists(avatarPath))
            return avatarPath;

        var primary = await GetPrimaryImageAsync(catalogItemId, versionLabel, ct);
        if (primary is null)
            return null;

        return await ResolveAssetPathAsync(primary.Id, ct);
    }

    public async Task ClearAvatarAsync(string catalogItemId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);

        DeleteAvatarFile(catalogItemId);

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE family_assets SET is_primary = 0 WHERE catalog_item_id = @itemId AND asset_type = 'Image'";
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private void DeleteAvatarFile(string catalogItemId)
    {
        try
        {
            var avatarPath = _pathResolver.GetAvatarPath(catalogItemId);
            if (File.Exists(avatarPath))
                File.Delete(avatarPath);
        }
        catch
        {
        }
    }

    public async Task<FamilyAsset?> GetPrimaryImageAsync(string catalogItemId, string? versionLabel = null, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        if (versionLabel is not null)
        {
            cmd.CommandText = "SELECT * FROM family_assets WHERE catalog_item_id = @itemId AND asset_type = 'Image' AND is_primary = 1 AND (version_label = @versionLabel OR version_label IS NULL) LIMIT 1";
            cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
            cmd.Parameters.Add(new SqliteParameter("@versionLabel", versionLabel));
        }
        else
        {
            cmd.CommandText = "SELECT * FROM family_assets WHERE catalog_item_id = @itemId AND asset_type = 'Image' AND is_primary = 1 LIMIT 1";
            cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        }
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct))
            return ReadAsset(reader);
        return null;
    }

    public async Task SetAssetVersionBindingAsync(string assetId, string? newVersionLabel, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        string? catalogItemId;
        string? assetTypeStr;
        string? oldRelativePath;
        string? oldVersionLabel;
        string? fileName;
        using (var readCmd = connection.CreateCommand())
        {
            readCmd.CommandText = "SELECT catalog_item_id, asset_type, relative_path, version_label, file_name FROM family_assets WHERE id = @id";
            readCmd.Parameters.Add(new SqliteParameter("@id", assetId));
            using var reader = await readCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct))
                return;
            catalogItemId = reader.GetString(0);
            assetTypeStr = reader.GetString(1);
            oldRelativePath = reader.GetString(2);
            oldVersionLabel = reader.IsDBNull(3) ? null : reader.GetString(3);
            fileName = reader.GetString(4);
        }

        if (string.Equals(oldVersionLabel, newVersionLabel, StringComparison.Ordinal))
            return;

        var assetType = (FamilyAssetType)Enum.Parse(typeof(FamilyAssetType), assetTypeStr!);
        var assetFolder = StoragePathResolver.GetAssetTypeFolder(assetType);

        var oldEffective = oldVersionLabel ?? "shared";
        var newEffective = newVersionLabel ?? "shared";

        _pathResolver.EnsureAssetDirectory(catalogItemId!, newEffective, assetFolder);
        var newDir = _pathResolver.GetAssetTypeDirectory(catalogItemId!, newEffective, assetFolder);
        var newDestPath = Path.Combine(newDir, fileName!);

        var counter = 1;
        while (File.Exists(newDestPath) && !string.Equals(newDestPath, Path.Combine(_database.GetDatabaseRoot(), oldRelativePath!), StringComparison.OrdinalIgnoreCase))
        {
            var nameWithoutExt = SafeFileName.GetBaseName(fileName!);
            var ext = Path.GetExtension(fileName);
            newDestPath = Path.Combine(newDir, nameWithoutExt + "_" + counter + ext);
            counter++;
        }

        var oldAbsolutePath = Path.Combine(_database.GetDatabaseRoot(), oldRelativePath!);

        await Task.Run(() =>
        {
            if (File.Exists(oldAbsolutePath))
            {
                File.Move(oldAbsolutePath, newDestPath);
                var oldDir = _pathResolver.GetAssetTypeDirectory(catalogItemId!, oldEffective, assetFolder);
                try
                {
                    if (Directory.Exists(oldDir) && !Directory.EnumerateFileSystemEntries(oldDir).Any())
                        Directory.Delete(oldDir);
                }
                catch { }
            }
        }, ct);

        var newRelativePath = _pathResolver.GetRelativePath(newDestPath);

        using (var updateCmd = connection.CreateCommand())
        {
            updateCmd.CommandText = "UPDATE family_assets SET version_label = @versionLabel, relative_path = @relativePath WHERE id = @id";
            updateCmd.Parameters.Add(new SqliteParameter("@versionLabel", (object?)newVersionLabel ?? DBNull.Value));
            updateCmd.Parameters.Add(new SqliteParameter("@relativePath", newRelativePath));
            updateCmd.Parameters.Add(new SqliteParameter("@id", assetId));
            await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static FamilyAsset ReadAsset(SqliteDataReader reader) => new(
        Id: reader.GetString(reader.GetOrdinal("id")),
        CatalogItemId: reader.GetString(reader.GetOrdinal("catalog_item_id")),
        VersionLabel: reader.IsDBNull(reader.GetOrdinal("version_label"))
            ? null
            : reader.GetString(reader.GetOrdinal("version_label")),
        AssetType: (FamilyAssetType)Enum.Parse(typeof(FamilyAssetType), reader.GetString(reader.GetOrdinal("asset_type"))),
        FileName: reader.GetString(reader.GetOrdinal("file_name")),
        RelativePath: reader.GetString(reader.GetOrdinal("relative_path")),
        SizeBytes: reader.GetInt64(reader.GetOrdinal("size_bytes")),
        Description: reader.IsDBNull(reader.GetOrdinal("description"))
            ? null
            : reader.GetString(reader.GetOrdinal("description")),
        CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at_utc"))),
        IsPrimary: !reader.IsDBNull(reader.GetOrdinal("is_primary")) && reader.GetInt64(reader.GetOrdinal("is_primary")) == 1);
}
