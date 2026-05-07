using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class LocalCategoryAttributeBindingService : ICategoryAttributeBindingService
{
    private readonly LocalCatalogDatabase _database;
    private readonly ICategoryRepository _categoryRepository;
    private readonly LocalCatalogMigrator _migrator;
    private string? _migratedDbPath;

    public LocalCategoryAttributeBindingService(LocalCatalogDatabase database, ICategoryRepository categoryRepository, LocalCatalogMigrator migrator)
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

    public async Task<IReadOnlyList<CategoryAttributeBinding>> GetBindingsForCategoryAsync(string categoryId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        var result = new List<CategoryAttributeBinding>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, category_id, attribute_id, sort_order, is_enabled FROM category_attribute_bindings WHERE category_id = @catId ORDER BY sort_order";
        cmd.Parameters.Add(new SqliteParameter("@catId", categoryId));

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new CategoryAttributeBinding(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4) != 0));
        }

        return result.AsReadOnly();
    }

    public async Task<IReadOnlyList<EffectiveCategoryAttribute>> GetEffectiveAttributesAsync(string? categoryId, CancellationToken ct = default)
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

        var merged = new Dictionary<string, EffectiveCategoryAttribute>();
        foreach (var ancestorId in ancestorIds)
        {
            var directBindings = await ReadDirectBindingsWithDefinitionAsync(connection, ancestorId, ct);
            foreach (var (binding, name, group) in directBindings)
            {
                var isInherited = ancestorId != categoryId;
                merged[binding.AttributeId] = new EffectiveCategoryAttribute(
                    binding.AttributeId,
                    name,
                    group,
                    binding.SortOrder,
                    binding.IsEnabled,
                    isInherited,
                    ancestorId);
            }
        }

        return merged.Values.OrderBy(a => a.SortOrder).ToList().AsReadOnly();
    }

    public async Task<CategoryAttributeBinding> CreateBindingAsync(string categoryId, string attributeId, int sortOrder, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        var id = Guid.NewGuid().ToString();

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO category_attribute_bindings (id, category_id, attribute_id, sort_order, is_enabled) VALUES (@id, @categoryId, @attributeId, @sortOrder, 1)";
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@categoryId", categoryId));
        cmd.Parameters.Add(new SqliteParameter("@attributeId", attributeId));
        cmd.Parameters.Add(new SqliteParameter("@sortOrder", sortOrder));
        await cmd.ExecuteNonQueryAsync(ct);

        return new CategoryAttributeBinding(id, categoryId, attributeId, sortOrder, true);
    }

    public async Task<bool> DeleteBindingAsync(string bindingId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM category_attribute_bindings WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", bindingId));
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    public async Task<CategoryAttributeBinding> UpdateBindingAsync(string bindingId, int? sortOrder, bool? isEnabled, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE category_attribute_bindings SET sort_order = COALESCE(@sortOrder, sort_order), is_enabled = COALESCE(@isEnabled, is_enabled) WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@sortOrder", (object?)sortOrder ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@isEnabled", isEnabled.HasValue ? (isEnabled.Value ? 1 : 0) : DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@id", bindingId));
        await cmd.ExecuteNonQueryAsync(ct);

        using var readCmd = connection.CreateCommand();
        readCmd.CommandText = "SELECT id, category_id, attribute_id, sort_order, is_enabled FROM category_attribute_bindings WHERE id = @id";
        readCmd.Parameters.Add(new SqliteParameter("@id", bindingId));

        using var reader = await readCmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new CategoryAttributeBinding(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt32(4) != 0);
    }

    public async Task<IReadOnlyList<CategoryAttributeBinding>> GetDirectBindingsAsync(string categoryId, CancellationToken ct = default)
    {
        return await GetBindingsForCategoryAsync(categoryId, ct);
    }

    public async Task<IReadOnlyList<CategoryAttributeBinding>> GetBindingsForAttributeAsync(string attributeId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        var result = new List<CategoryAttributeBinding>();

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, category_id, attribute_id, sort_order, is_enabled FROM category_attribute_bindings WHERE attribute_id = @attrId ORDER BY sort_order";
        cmd.Parameters.Add(new SqliteParameter("@attrId", attributeId));

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new CategoryAttributeBinding(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4) != 0));
        }

        return result.AsReadOnly();
    }

    public async Task DeleteBindingsForAttributeAsync(string attributeId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM category_attribute_bindings WHERE attribute_id = @attrId";
        cmd.Parameters.Add(new SqliteParameter("@attrId", attributeId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<(CategoryAttributeBinding Binding, string Name, string? Group)>> ReadDirectBindingsWithDefinitionAsync(SqliteConnection connection, string? categoryId, CancellationToken ct)
    {
        var result = new List<(CategoryAttributeBinding, string, string?)>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT cab.id, cab.category_id, cab.attribute_id, cab.sort_order, cab.is_enabled, ad.name, ad.group_name FROM category_attribute_bindings cab JOIN attribute_definitions ad ON cab.attribute_id = ad.id WHERE cab.category_id IS @catId ORDER BY cab.sort_order";
        cmd.Parameters.Add(new SqliteParameter("@catId", (object?)categoryId ?? DBNull.Value));

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var binding = new CategoryAttributeBinding(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4) != 0);
            var name = reader.GetString(5);
            var group = reader.IsDBNull(6) ? null : reader.GetString(6);
            result.Add((binding, name, group));
        }

        return result;
    }
}
