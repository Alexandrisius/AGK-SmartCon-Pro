using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class LocalAttributePresetService : IAttributePresetService
{
    private readonly LocalCatalogDatabase _database;
    private readonly ICategoryRepository _categoryRepository;
    private readonly LocalCatalogMigrator _migrator;
    private string? _migratedDbPath;

    public LocalAttributePresetService(LocalCatalogDatabase database, ICategoryRepository categoryRepository, LocalCatalogMigrator migrator)
    {
        _database = database;
        _categoryRepository = categoryRepository;
        _migrator = migrator;
    }

    private async Task EnsureMigratedAsync(CancellationToken ct)
    {
        var currentPath = _database.GetDatabaseRoot();
        if (_migratedDbPath == currentPath) return;
        await _migrator.MigrateAsync(ct);
        _migratedDbPath = currentPath;
    }

    public async Task<IReadOnlyList<AttributePreset>> GetAllPresetsAsync(CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        var presets = await ReadAllPresetsAsync(connection, ct);
        return presets.AsReadOnly();
    }

    public async Task<AttributePreset?> GetPresetForCategoryAsync(string? categoryId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, category_id, created_at_utc, updated_at_utc FROM attribute_presets WHERE category_id IS @categoryId";
        cmd.Parameters.Add(new SqliteParameter("@categoryId", (object?)categoryId ?? DBNull.Value));

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var id = reader.GetString(0);
        var catId = reader.IsDBNull(1) ? null : reader.GetString(1);
        var createdAt = DateTimeOffset.Parse(reader.GetString(2));
        var updatedAt = DateTimeOffset.Parse(reader.GetString(3));

        var parameters = await ReadParametersAsync(connection, id, ct);
        return new AttributePreset(id, catId, parameters, createdAt, updatedAt);
    }

    public async Task<IReadOnlyList<AttributePresetParameter>> GetEffectiveParametersAsync(string? categoryId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        var allCategories = await _categoryRepository.GetAllAsync(ct);
        var byId = allCategories.ToDictionary(c => c.Id);

        var ancestorIds = new List<string?>();
        if (categoryId is not null)
        {
            var chain = new List<string>();
            var current = categoryId;
            while (current is not null && byId.TryGetValue(current, out var node))
            {
                chain.Add(current);
                current = node.ParentId;
            }

            chain.Reverse();
            ancestorIds.AddRange(chain);
        }

        ancestorIds.Add(null);

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        var merged = new Dictionary<string, AttributePresetParameter>();
        foreach (var ancestorId in ancestorIds)
        {
            var preset = await ReadPresetByCategoryIdAsync(connection, ancestorId, ct);
            if (preset is null)
                continue;

            foreach (var param in preset.Parameters)
                merged[param.ParameterName] = param;
        }

        return merged.Values.OrderBy(p => p.SortOrder).ToList().AsReadOnly();
    }

    public async Task<AttributePreset> CreatePresetAsync(string? categoryId, IReadOnlyList<AttributePresetParameter> parameters, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        var id = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();

        try
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO attribute_presets (id, category_id, created_at_utc, updated_at_utc) VALUES (@id, @categoryId, @createdAt, @updatedAt)";
                cmd.Parameters.Add(new SqliteParameter("@id", id));
                cmd.Parameters.Add(new SqliteParameter("@categoryId", (object?)categoryId ?? DBNull.Value));
                cmd.Parameters.Add(new SqliteParameter("@createdAt", now.ToString("o")));
                cmd.Parameters.Add(new SqliteParameter("@updatedAt", now.ToString("o")));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await InsertParametersAsync(connection, id, parameters, ct);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        return new AttributePreset(id, categoryId, parameters, now, now);
    }

    public async Task UpdatePresetAsync(string presetId, IReadOnlyList<AttributePresetParameter> parameters, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        var now = DateTimeOffset.UtcNow;

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();

        try
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE attribute_presets SET updated_at_utc = @updatedAt WHERE id = @id";
                cmd.Parameters.Add(new SqliteParameter("@updatedAt", now.ToString("o")));
                cmd.Parameters.Add(new SqliteParameter("@id", presetId));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            using (var delCmd = connection.CreateCommand())
            {
                delCmd.CommandText = "DELETE FROM attribute_preset_parameters WHERE preset_id = @presetId";
                delCmd.Parameters.Add(new SqliteParameter("@presetId", presetId));
                await delCmd.ExecuteNonQueryAsync(ct);
            }

            await InsertParametersAsync(connection, presetId, parameters, ct);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task DeletePresetAsync(string presetId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM attribute_presets WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", presetId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<AttributePreset?> ReadPresetByCategoryIdAsync(SqliteConnection connection, string? categoryId, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, category_id, created_at_utc, updated_at_utc FROM attribute_presets WHERE category_id IS @categoryId";
        cmd.Parameters.Add(new SqliteParameter("@categoryId", (object?)categoryId ?? DBNull.Value));

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var id = reader.GetString(0);
        var catId = reader.IsDBNull(1) ? null : reader.GetString(1);
        var createdAt = DateTimeOffset.Parse(reader.GetString(2));
        var updatedAt = DateTimeOffset.Parse(reader.GetString(3));

        var parameters = await ReadParametersAsync(connection, id, ct);
        return new AttributePreset(id, catId, parameters, createdAt, updatedAt);
    }

    private static async Task<List<AttributePreset>> ReadAllPresetsAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, category_id, created_at_utc, updated_at_utc FROM attribute_presets ORDER BY created_at_utc";

        var presets = new List<AttributePreset>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            var catId = reader.IsDBNull(1) ? null : reader.GetString(1);
            var createdAt = DateTimeOffset.Parse(reader.GetString(2));
            var updatedAt = DateTimeOffset.Parse(reader.GetString(3));

            presets.Add(new AttributePreset(id, catId, [], createdAt, updatedAt));
        }

        var result = new List<AttributePreset>(presets.Count);
        foreach (var preset in presets)
        {
            var parameters = await ReadParametersAsync(connection, preset.Id, ct);
            result.Add(preset with { Parameters = parameters });
        }

        return result;
    }

    private static async Task<IReadOnlyList<AttributePresetParameter>> ReadParametersAsync(SqliteConnection connection, string presetId, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT parameter_name, display_name, sort_order FROM attribute_preset_parameters WHERE preset_id = @presetId ORDER BY sort_order";
        cmd.Parameters.Add(new SqliteParameter("@presetId", presetId));

        var result = new List<AttributePresetParameter>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new AttributePresetParameter(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt32(2)));
        }

        return result.AsReadOnly();
    }

    private static async Task InsertParametersAsync(SqliteConnection connection, string presetId, IReadOnlyList<AttributePresetParameter> parameters, CancellationToken ct)
    {
        foreach (var param in parameters)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO attribute_preset_parameters (preset_id, parameter_name, display_name, sort_order) VALUES (@presetId, @paramName, @displayName, @sortOrder)";
            cmd.Parameters.Add(new SqliteParameter("@presetId", presetId));
            cmd.Parameters.Add(new SqliteParameter("@paramName", param.ParameterName));
            cmd.Parameters.Add(new SqliteParameter("@displayName", (object?)param.DisplayName ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@sortOrder", param.SortOrder));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
