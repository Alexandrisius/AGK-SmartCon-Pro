using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class LocalFamilyAssetService : IFamilyAssetService
{
    private readonly LocalCatalogDatabase _database;
    private readonly StoragePathResolver _pathResolver;

    public LocalFamilyAssetService(LocalCatalogDatabase database, StoragePathResolver pathResolver)
    {
        _database = database;
        _pathResolver = pathResolver;
    }

    public async Task<FamilyAsset> AddAssetAsync(string catalogItemId, string? versionLabel, FamilyAssetType assetType, string sourceFilePath, string? description, CancellationToken ct = default)
    {
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
            var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            destPath = Path.Combine(destDir, $"{nameWithoutExt}_{counter}{ext}");
            counter++;
        }

        File.Copy(sourceFilePath, destPath);

        var relativePath = _pathResolver.GetRelativePath(destPath);
        var now = DateTimeOffset.UtcNow;

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO family_assets (id, catalog_item_id, version_label, asset_type, file_name, relative_path, size_bytes, description, created_at_utc)
            VALUES (@id, @catalogItemId, @versionLabel, @assetType, @fileName, @relativePath, @sizeBytes, @description, @createdAtUtc)
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
        await cmd.ExecuteNonQueryAsync(ct);

        return new FamilyAsset(id, catalogItemId, versionLabel, assetType, Path.GetFileName(destPath), relativePath, fileInfo.Length, description, now);
    }

    public async Task<IReadOnlyList<FamilyAsset>> GetAssetsAsync(string catalogItemId, string? versionLabel = null, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
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
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            assets.Add(ReadAsset(reader));
        }

        return assets;
    }

    public async Task<bool> DeleteAssetAsync(string assetId, CancellationToken ct = default)
    {
        string? relativePath;
        using (var connection = _database.CreateConnection())
        {
            await connection.OpenAsync(ct);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT relative_path FROM family_assets WHERE id = @id";
            cmd.Parameters.Add(new SqliteParameter("@id", assetId));
            relativePath = await cmd.ExecuteScalarAsync(ct) as string;
        }

        if (relativePath is null)
            return false;

        using (var connection = _database.CreateConnection())
        {
            await connection.OpenAsync(ct);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM family_assets WHERE id = @id";
            cmd.Parameters.Add(new SqliteParameter("@id", assetId));
            var rows = await cmd.ExecuteNonQueryAsync(ct);

            if (rows > 0)
            {
                var absolutePath = Path.Combine(_database.GetDatabaseRoot(), relativePath);
                try
                {
                    if (File.Exists(absolutePath))
                        File.Delete(absolutePath);
                }
                catch
                {
                }
            }

            return rows > 0;
        }
    }

    public async Task<string?> ResolveAssetPathAsync(string assetId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT relative_path FROM family_assets WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", assetId));

        var relativePath = await cmd.ExecuteScalarAsync(ct) as string;
        if (relativePath is null)
            return null;

        var absolutePath = Path.Combine(_database.GetDatabaseRoot(), relativePath);
        return File.Exists(absolutePath) ? absolutePath : null;
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
        CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at_utc"))));
}
