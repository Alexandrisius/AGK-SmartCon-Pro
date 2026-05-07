using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class LocalAttributeDefinitionRepository : IAttributeDefinitionRepository
{
    private readonly LocalCatalogDatabase _database;

    public LocalAttributeDefinitionRepository(LocalCatalogDatabase database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<AttributeDefinition>> GetAllAsync(CancellationToken ct = default)
    {
        var result = new List<AttributeDefinition>();

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, group_name, is_active, created_at_utc FROM attribute_definitions ORDER BY name";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(ReadDefinition(reader));
        }

        return result.AsReadOnly();
    }

    public async Task<AttributeDefinition?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, group_name, is_active, created_at_utc FROM attribute_definitions WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return ReadDefinition(reader);
    }

    public async Task<AttributeDefinition?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, group_name, is_active, created_at_utc FROM attribute_definitions WHERE name = @name COLLATE NOCASE";
        cmd.Parameters.Add(new SqliteParameter("@name", name));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return ReadDefinition(reader);
    }

    public async Task<AttributeDefinition> CreateAsync(string name, string? group, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO attribute_definitions (id, name, group_name, is_active, created_at_utc) VALUES (@id, @name, @group, @isActive, @createdAt)";
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@name", name));
        cmd.Parameters.Add(new SqliteParameter("@group", (object?)group ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@isActive", 1));
        cmd.Parameters.Add(new SqliteParameter("@createdAt", now.ToString("o")));
        await cmd.ExecuteNonQueryAsync(ct);

        return new AttributeDefinition(id, name, group, true, now);
    }

    public async Task<AttributeDefinition> UpdateAsync(string id, string? name, string? group, bool? isActive, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE attribute_definitions SET name = COALESCE(@name, name), group_name = COALESCE(@group, group_name), is_active = COALESCE(@isActive, is_active) WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@name", (object?)name ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@group", (object?)group ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@isActive", isActive.HasValue ? (isActive.Value ? 1 : 0) : DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        await cmd.ExecuteNonQueryAsync(ct);

        using var selectCmd = connection.CreateCommand();
        selectCmd.CommandText = "SELECT id, name, group_name, is_active, created_at_utc FROM attribute_definitions WHERE id = @id";
        selectCmd.Parameters.Add(new SqliteParameter("@id", id));
        using var reader = await selectCmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return ReadDefinition(reader);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM attribute_definitions WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    public async Task<bool> NameExistsAsync(string name, string? excludeId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM attribute_definitions WHERE name = @name COLLATE NOCASE AND (@excludeId IS NULL OR id != @excludeId)";
        cmd.Parameters.Add(new SqliteParameter("@name", name));
        cmd.Parameters.Add(new SqliteParameter("@excludeId", (object?)excludeId ?? DBNull.Value));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l && l > 0;
    }

    private static AttributeDefinition ReadDefinition(SqliteDataReader reader)
    {
        return new AttributeDefinition(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt32(3) != 0,
            DateTimeOffset.Parse(reader.GetString(4)));
    }
}
