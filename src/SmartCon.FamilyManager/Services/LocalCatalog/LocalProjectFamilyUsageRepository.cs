using System;
using System.Linq;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
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
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO project_usage (id, catalog_item_id, version_id, loaded_version_label, project_name, project_path, revit_major_version, action, created_at_utc)
            VALUES (@id, @catalogItemId, @versionId, @loadedVersionLabel, @projectName, @projectPath, @revitVersion, @action, @createdAtUtc)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", usage.Id));
        cmd.Parameters.Add(new SqliteParameter("@catalogItemId", usage.CatalogItemId));
        cmd.Parameters.Add(new SqliteParameter("@versionId", (object?)usage.VersionId ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@loadedVersionLabel", (object?)usage.LoadedVersionLabel ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@projectName", (object?)usage.ProjectName ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@projectPath", (object?)usage.ProjectPath ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@revitVersion", (object?)usage.RevitMajorVersion ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@action", usage.Action));
        cmd.Parameters.Add(new SqliteParameter("@createdAtUtc", usage.CreatedAtUtc.ToString("o")));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProjectFamilyUsage>> GetUsageForItemAsync(string catalogItemId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM project_usage WHERE catalog_item_id = @itemId ORDER BY created_at_utc DESC";
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));

        var results = new List<ProjectFamilyUsage>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(ReadUsage(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<ProjectFamilyUsage>> GetUsageForProjectAsync(string projectFingerprint, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
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

    public async Task<IReadOnlyDictionary<string, string?>> GetLoadedVersionLabelsAsync(string projectFingerprint, IEnumerable<string> catalogItemIds, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT catalog_item_id, loaded_version_label
            FROM project_usage
            WHERE project_path = @fingerprint AND catalog_item_id IN (@itemIds) AND loaded_version_label IS NOT NULL
            ORDER BY created_at_utc DESC
            """;
        cmd.Parameters.Add(new SqliteParameter("@fingerprint", projectFingerprint));
        // SQLite does not support array parameters directly; build IN clause dynamically for small sets.
        var ids = catalogItemIds.ToList();
        if (ids.Count == 0) return new Dictionary<string, string?>();

        var placeholders = string.Join(",", ids.Select((_, i) => $"@id{i}"));
        cmd.CommandText = cmd.CommandText.Replace("@itemIds", placeholders);
        for (var i = 0; i < ids.Count; i++)
        {
            cmd.Parameters.Add(new SqliteParameter($"@id{i}", ids[i]));
        }

        var result = new Dictionary<string, string?>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var itemId = reader.GetString(0);
            var label = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (!result.ContainsKey(itemId))
                result[itemId] = label;
        }
        return result;
    }

    public async Task<int> DeleteOldUsagesAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(maxAge);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM project_usage WHERE created_at_utc < @cutoff";
        cmd.Parameters.Add(new SqliteParameter("@cutoff", cutoff.ToString("o")));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static ProjectFamilyUsage ReadUsage(SqliteDataReader reader) => new(
        Id: reader.GetString(reader.GetOrdinal("id")),
        CatalogItemId: reader.GetString(reader.GetOrdinal("catalog_item_id")),
        VersionId: reader.IsDBNull(reader.GetOrdinal("version_id"))
            ? null
            : reader.GetString(reader.GetOrdinal("version_id")),
        LoadedVersionLabel: reader.IsDBNull(reader.GetOrdinal("loaded_version_label"))
            ? null
            : reader.GetString(reader.GetOrdinal("loaded_version_label")),
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
