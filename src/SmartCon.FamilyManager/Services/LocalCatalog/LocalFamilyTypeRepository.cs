using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class LocalFamilyTypeRepository : IFamilyTypeRepository
{
    private readonly LocalCatalogDatabase _database;

    public LocalFamilyTypeRepository(LocalCatalogDatabase database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemAsync(string catalogItemId, CancellationToken ct = default)
    {
        var result = new List<FamilyTypeDescriptor>();

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, type_name, sort_order FROM family_types WHERE catalog_item_id = @itemId ORDER BY sort_order";
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new FamilyTypeDescriptor(
                reader.GetString(0),
                catalogItemId,
                reader.GetString(1),
                reader.GetInt32(2)));
        }

        return result.AsReadOnly();
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<FamilyTypeDescriptor>>> GetAllTypesBatchAsync(IEnumerable<string> catalogItemIds, CancellationToken ct = default)
    {
        var idList = catalogItemIds.ToList();
        if (idList.Count == 0) return new Dictionary<string, IReadOnlyList<FamilyTypeDescriptor>>();

        var result = new Dictionary<string, IReadOnlyList<FamilyTypeDescriptor>>();

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        var placeholders = string.Join(",", Enumerable.Range(0, idList.Count).Select(i => $"@p{i}"));
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT id, catalog_item_id, type_name, sort_order FROM family_types WHERE catalog_item_id IN ({placeholders}) ORDER BY sort_order";
        for (var i = 0; i < idList.Count; i++)
            cmd.Parameters.Add(new SqliteParameter($"@p{i}", idList[i]));

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var itemId = reader.GetString(1);
            var type = new FamilyTypeDescriptor(
                reader.GetString(0),
                itemId,
                reader.GetString(2),
                reader.GetInt32(3));

            if (!result.TryGetValue(itemId, out var list))
            {
                list = new List<FamilyTypeDescriptor>();
                result[itemId] = list;
            }
            ((List<FamilyTypeDescriptor>)list).Add(type);
        }

        return result;
    }

    public async Task SaveTypesAsync(string catalogItemId, IReadOnlyList<FamilyTypeDescriptor> types, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();

        try
        {
            using (var delCmd = connection.CreateCommand())
            {
                delCmd.CommandText = "DELETE FROM family_types WHERE catalog_item_id = @itemId";
                delCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                await delCmd.ExecuteNonQueryAsync(ct);
            }

            for (var i = 0; i < types.Count; i++)
            {
                using var insertCmd = connection.CreateCommand();
                insertCmd.CommandText = "INSERT INTO family_types (id, catalog_item_id, type_name, sort_order) VALUES (@id, @itemId, @name, @sort)";
                insertCmd.Parameters.Add(new SqliteParameter("@id", types[i].Id));
                insertCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                insertCmd.Parameters.Add(new SqliteParameter("@name", types[i].Name));
                insertCmd.Parameters.Add(new SqliteParameter("@sort", i));
                await insertCmd.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<bool> HasTypesAsync(string catalogItemId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM family_types WHERE catalog_item_id = @itemId";
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l && l > 0;
    }
}
