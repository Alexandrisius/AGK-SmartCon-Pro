using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class LocalProjectFamilyUsageRepository : IProjectFamilyUsageRepository
{
    private readonly LocalCatalogDatabase _database;

    public LocalProjectFamilyUsageRepository(LocalCatalogDatabase database)
    {
        _database = database;
    }

    public async Task RecordUsageAsync(ProjectFamilyUsage usage, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO project_usage (id, catalog_item_id, version_id, project_name, project_path, revit_major_version, action, created_at_utc)
            VALUES (@id, @catalogItemId, @versionId, @projectName, @projectPath, @revitVersion, @action, @createdAtUtc)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", usage.Id));
        cmd.Parameters.Add(new SqliteParameter("@catalogItemId", usage.CatalogItemId));
        cmd.Parameters.Add(new SqliteParameter("@versionId", (object?)usage.VersionId ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@projectName", (object?)usage.ProjectName ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@projectPath", (object?)usage.ProjectPath ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@revitVersion", (object?)usage.RevitMajorVersion ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@action", usage.Action));
        cmd.Parameters.Add(new SqliteParameter("@createdAtUtc", usage.CreatedAtUtc.ToString("o")));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ProjectFamilyUsage>> GetUsageForItemAsync(string catalogItemId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM project_usage WHERE catalog_item_id = @itemId ORDER BY created_at_utc DESC";
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));

        var results = new List<ProjectFamilyUsage>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadUsage(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<ProjectFamilyUsage>> GetUsageForProjectAsync(string projectFingerprint, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM project_usage WHERE project_path = @fingerprint ORDER BY created_at_utc DESC";
        cmd.Parameters.Add(new SqliteParameter("@fingerprint", projectFingerprint));

        var results = new List<ProjectFamilyUsage>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadUsage(reader));
        }

        return results;
    }

    private static ProjectFamilyUsage ReadUsage(SqliteDataReader reader) => new(
        Id: reader.GetString(reader.GetOrdinal("id")),
        CatalogItemId: reader.GetString(reader.GetOrdinal("catalog_item_id")),
        VersionId: reader.IsDBNull(reader.GetOrdinal("version_id"))
            ? null
            : reader.GetString(reader.GetOrdinal("version_id")),
        ProjectName: reader.IsDBNull(reader.GetOrdinal("project_name"))
            ? null
            : reader.GetString(reader.GetOrdinal("project_name")),
        ProjectPath: reader.IsDBNull(reader.GetOrdinal("project_path"))
            ? null
            : reader.GetString(reader.GetOrdinal("project_path")),
        RevitMajorVersion: reader.IsDBNull(reader.GetOrdinal("revit_major_version"))
            ? null
            : reader.GetInt32(reader.GetOrdinal("revit_major_version")),
        Action: reader.GetString(reader.GetOrdinal("action")),
        CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at_utc"))));
}
