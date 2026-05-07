using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class LocalFamilyDataImportRunRepository : IFamilyDataImportRunRepository
{
    private readonly LocalCatalogDatabase _database;

    public LocalFamilyDataImportRunRepository(LocalCatalogDatabase database)
    {
        _database = database;
    }

    public async Task<FamilyDataImportRun?> GetLatestRunAsync(string catalogItemId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, catalog_item_id, version_id, file_id, source_sha256, revit_major_version, status, types_count, started_at_utc, completed_at_utc, error_message FROM family_data_import_runs WHERE catalog_item_id = @itemId ORDER BY started_at_utc DESC LIMIT 1";
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return ReadRun(reader);

        return null;
    }

    public async Task<IReadOnlyList<FamilyDataImportRun>> GetRunsForItemAsync(string catalogItemId, CancellationToken ct = default)
    {
        var result = new List<FamilyDataImportRun>();

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, catalog_item_id, version_id, file_id, source_sha256, revit_major_version, status, types_count, started_at_utc, completed_at_utc, error_message FROM family_data_import_runs WHERE catalog_item_id = @itemId ORDER BY started_at_utc DESC";
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(ReadRun(reader));

        return result.AsReadOnly();
    }

    public async Task<FamilyDataImportRun> CreateRunAsync(FamilyDataImportRun run, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO family_data_import_runs (id, catalog_item_id, version_id, file_id, source_sha256, revit_major_version, status, types_count, started_at_utc, completed_at_utc, error_message) VALUES (@id, @catalogItemId, @versionId, @fileId, @sourceSha256, @revitMajorVersion, @status, @typesCount, @startedAtUtc, @completedAtUtc, @errorMessage)";
        cmd.Parameters.Add(new SqliteParameter("@id", run.Id));
        cmd.Parameters.Add(new SqliteParameter("@catalogItemId", run.CatalogItemId));
        cmd.Parameters.Add(new SqliteParameter("@versionId", (object?)run.VersionId ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@fileId", (object?)run.FileId ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@sourceSha256", (object?)run.SourceSha256 ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@revitMajorVersion", run.RevitMajorVersion));
        cmd.Parameters.Add(new SqliteParameter("@status", run.Status.ToString()));
        cmd.Parameters.Add(new SqliteParameter("@typesCount", run.TypesCount));
        cmd.Parameters.Add(new SqliteParameter("@startedAtUtc", run.StartedAtUtc.ToString("o")));
        cmd.Parameters.Add(new SqliteParameter("@completedAtUtc", run.CompletedAtUtc.HasValue ? run.CompletedAtUtc.Value.ToString("o") : (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@errorMessage", (object?)run.ErrorMessage ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);
        return run;
    }

    public async Task<FamilyDataImportRun> UpdateRunAsync(string runId, FamilyDataImportStatus status, int typesCount, DateTimeOffset completedAtUtc, string? errorMessage, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE family_data_import_runs SET status = @status, types_count = @typesCount, completed_at_utc = @completedAtUtc, error_message = @errorMessage WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", runId));
        cmd.Parameters.Add(new SqliteParameter("@status", status.ToString()));
        cmd.Parameters.Add(new SqliteParameter("@typesCount", typesCount));
        cmd.Parameters.Add(new SqliteParameter("@completedAtUtc", completedAtUtc.ToString("o")));
        cmd.Parameters.Add(new SqliteParameter("@errorMessage", (object?)errorMessage ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);

        using var selectCmd = connection.CreateCommand();
        selectCmd.CommandText = "SELECT id, catalog_item_id, version_id, file_id, source_sha256, revit_major_version, status, types_count, started_at_utc, completed_at_utc, error_message FROM family_data_import_runs WHERE id = @id";
        selectCmd.Parameters.Add(new SqliteParameter("@id", runId));
        using var reader = await selectCmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return ReadRun(reader);
    }

    private static FamilyDataImportRun ReadRun(SqliteDataReader reader)
    {
        return new FamilyDataImportRun(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt32(5),
            (FamilyDataImportStatus)Enum.Parse(typeof(FamilyDataImportStatus), reader.GetString(6)),
            reader.GetInt32(7),
            DateTimeOffset.Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10));
    }
}
