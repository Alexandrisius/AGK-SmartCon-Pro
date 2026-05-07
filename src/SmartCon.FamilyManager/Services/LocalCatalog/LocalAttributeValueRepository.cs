using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class LocalAttributeValueRepository : IAttributeValueRepository
{
    private readonly LocalCatalogDatabase _database;

    public LocalAttributeValueRepository(LocalCatalogDatabase database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<ExtractedAttributeValue>> GetValuesForItemAsync(string catalogItemId, string? versionId, CancellationToken ct = default)
    {
        var result = new List<ExtractedAttributeValue>();

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();

        if (versionId is null)
        {
            cmd.CommandText = "SELECT id, catalog_item_id, version_id, file_id, type_id, attribute_id, binding_id, parameter_name, parameter_scope, storage_type, value_text, value_raw, value_number, unit_type_id, status, message, extraction_run_id, extracted_at_utc FROM extracted_attribute_values WHERE catalog_item_id = @itemId AND version_id IS NULL";
            cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        }
        else
        {
            cmd.CommandText = "SELECT id, catalog_item_id, version_id, file_id, type_id, attribute_id, binding_id, parameter_name, parameter_scope, storage_type, value_text, value_raw, value_number, unit_type_id, status, message, extraction_run_id, extracted_at_utc FROM extracted_attribute_values WHERE catalog_item_id = @itemId AND version_id = @versionId";
            cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
            cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
        }

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(ReadValue(reader));

        return result.AsReadOnly();
    }

    public async Task<IReadOnlyList<ExtractedAttributeValue>> GetValuesForTypeAsync(string typeId, CancellationToken ct = default)
    {
        var result = new List<ExtractedAttributeValue>();

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, catalog_item_id, version_id, file_id, type_id, attribute_id, binding_id, parameter_name, parameter_scope, storage_type, value_text, value_raw, value_number, unit_type_id, status, message, extraction_run_id, extracted_at_utc FROM extracted_attribute_values WHERE type_id = @typeId";
        cmd.Parameters.Add(new SqliteParameter("@typeId", typeId));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(ReadValue(reader));

        return result.AsReadOnly();
    }

    public async Task<IReadOnlyList<ExtractedAttributeValue>> GetValuesForRunAsync(string runId, CancellationToken ct = default)
    {
        var result = new List<ExtractedAttributeValue>();

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, catalog_item_id, version_id, file_id, type_id, attribute_id, binding_id, parameter_name, parameter_scope, storage_type, value_text, value_raw, value_number, unit_type_id, status, message, extraction_run_id, extracted_at_utc FROM extracted_attribute_values WHERE extraction_run_id = @runId";
        cmd.Parameters.Add(new SqliteParameter("@runId", runId));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(ReadValue(reader));

        return result.AsReadOnly();
    }

    public async Task SaveValuesAsync(IReadOnlyList<ExtractedAttributeValue> values, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();

        try
        {
            foreach (var v in values)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"INSERT OR REPLACE INTO extracted_attribute_values
                    (id, catalog_item_id, version_id, file_id, type_id, attribute_id, binding_id, parameter_name, parameter_scope, storage_type, value_text, value_raw, value_number, unit_type_id, status, message, extraction_run_id, extracted_at_utc)
                    VALUES (@id, @itemId, @versionId, @fileId, @typeId, @attributeId, @bindingId, @paramName, @paramScope, @storageType, @valueText, @valueRaw, @valueNumber, @unitTypeId, @status, @message, @runId, @extractedAt)";
                AddParameters(cmd, v);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task ReplaceSnapshotAsync(string catalogItemId, string? versionId, string runId, IReadOnlyList<ExtractedAttributeValue> values, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();

        try
        {
            if (versionId is null)
            {
                using var delCmd = connection.CreateCommand();
                delCmd.CommandText = "DELETE FROM extracted_attribute_values WHERE catalog_item_id = @itemId AND version_id IS NULL";
                delCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                await delCmd.ExecuteNonQueryAsync(ct);
            }
            else
            {
                using var delCmd = connection.CreateCommand();
                delCmd.CommandText = "DELETE FROM extracted_attribute_values WHERE catalog_item_id = @itemId AND version_id = @versionId";
                delCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                delCmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
                await delCmd.ExecuteNonQueryAsync(ct);
            }

            foreach (var v in values)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"INSERT INTO extracted_attribute_values
                    (id, catalog_item_id, version_id, file_id, type_id, attribute_id, binding_id, parameter_name, parameter_scope, storage_type, value_text, value_raw, value_number, unit_type_id, status, message, extraction_run_id, extracted_at_utc)
                    VALUES (@id, @itemId, @versionId, @fileId, @typeId, @attributeId, @bindingId, @paramName, @paramScope, @storageType, @valueText, @valueRaw, @valueNumber, @unitTypeId, @status, @message, @runId, @extractedAt)";
                AddParameters(cmd, v);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<int> DeleteValuesForRunAsync(string runId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM extracted_attribute_values WHERE extraction_run_id = @runId";
        cmd.Parameters.Add(new SqliteParameter("@runId", runId));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> GetFoundCountAsync(string catalogItemId, string? versionId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();

        if (versionId is null)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM extracted_attribute_values WHERE catalog_item_id = @itemId AND status = 'Found' AND version_id IS NULL";
            cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        }
        else
        {
            cmd.CommandText = "SELECT COUNT(*) FROM extracted_attribute_values WHERE catalog_item_id = @itemId AND status = 'Found' AND version_id = @versionId";
            cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
            cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
        }

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? (int)l : 0;
    }

    public async Task<int> GetMissingCountAsync(string catalogItemId, string? versionId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();

        if (versionId is null)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM extracted_attribute_values WHERE catalog_item_id = @itemId AND status != 'Found' AND version_id IS NULL";
            cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        }
        else
        {
            cmd.CommandText = "SELECT COUNT(*) FROM extracted_attribute_values WHERE catalog_item_id = @itemId AND status != 'Found' AND version_id = @versionId";
            cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
            cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
        }

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? (int)l : 0;
    }

    private static ExtractedAttributeValue ReadValue(SqliteDataReader reader)
    {
        return new ExtractedAttributeValue(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : (AttributeScope?)Enum.Parse(typeof(AttributeScope), reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : (double?)reader.GetDouble(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            (AttributeValueStatus)Enum.Parse(typeof(AttributeValueStatus), reader.GetString(14)),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.GetString(16),
            DateTimeOffset.Parse(reader.GetString(17)));
    }

    private static void AddParameters(SqliteCommand cmd, ExtractedAttributeValue v)
    {
        cmd.Parameters.Add(new SqliteParameter("@id", v.Id));
        cmd.Parameters.Add(new SqliteParameter("@itemId", v.CatalogItemId));
        cmd.Parameters.Add(new SqliteParameter("@versionId", (object?)v.VersionId ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@fileId", (object?)v.FileId ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@typeId", (object?)v.TypeId ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@attributeId", v.AttributeId));
        cmd.Parameters.Add(new SqliteParameter("@bindingId", (object?)v.BindingId ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@paramName", v.ParameterName));
        cmd.Parameters.Add(new SqliteParameter("@paramScope", v.ParameterScope.HasValue ? v.ParameterScope.Value.ToString() : DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@storageType", (object?)v.StorageType ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@valueText", (object?)v.ValueText ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@valueRaw", (object?)v.ValueRaw ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@valueNumber", (object?)v.ValueNumber ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@unitTypeId", (object?)v.UnitTypeId ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@status", v.Status.ToString()));
        cmd.Parameters.Add(new SqliteParameter("@message", (object?)v.Message ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@runId", v.ExtractionRunId));
        cmd.Parameters.Add(new SqliteParameter("@extractedAt", v.ExtractedAtUtc.ToString("O")));
    }
}
