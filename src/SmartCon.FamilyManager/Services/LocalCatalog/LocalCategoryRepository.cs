using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class LocalCategoryRepository : ICategoryRepository
{
    private readonly LocalCatalogDatabase _database;

    public LocalCategoryRepository(LocalCatalogDatabase database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<CategoryNode>> GetAllAsync(CancellationToken ct = default)
    {
        var rawNodes = new List<RawCategory>();

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, parent_id, sort_order, created_at_utc FROM categories ORDER BY sort_order, name";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rawNodes.Add(new RawCategory(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3),
                DateTimeOffset.Parse(reader.GetString(4))));
        }

        var byId = rawNodes.ToDictionary(n => n.Id);
        var result = new List<CategoryNode>(rawNodes.Count);

        foreach (var raw in rawNodes)
        {
            var fullPath = BuildFullPath(byId, raw.Id);
            result.Add(new CategoryNode(raw.Id, raw.Name, raw.ParentId, raw.SortOrder, fullPath, raw.CreatedAt));
        }

        return result.AsReadOnly();
    }

    public async Task<CategoryNode?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        var raw = await ReadRawByIdAsync(connection, id, ct);
        if (raw is null)
            return null;

        var byId = await LoadAllRawAsync(connection, ct);
        var fullPath = BuildFullPath(byId, raw.Id);
        return new CategoryNode(raw.Id, raw.Name, raw.ParentId, raw.SortOrder, fullPath, raw.CreatedAt);
    }

    public async Task<CategoryNode> AddAsync(string name, string? parentId, int sortOrder, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString();
        var createdAt = DateTimeOffset.UtcNow;

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO categories (id, name, parent_id, sort_order, created_at_utc) VALUES (@id, @name, @parentId, @sortOrder, @createdAt)";
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@name", name));
            cmd.Parameters.Add(new SqliteParameter("@parentId", (object?)parentId ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@sortOrder", sortOrder));
            cmd.Parameters.Add(new SqliteParameter("@createdAt", createdAt.ToString("o")));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var byId = await LoadAllRawAsync(connection, ct);
        var fullPath = BuildFullPath(byId, id);
        return new CategoryNode(id, name, parentId, sortOrder, fullPath, createdAt);
    }

    public async Task<CategoryNode?> RenameAsync(string id, string newName, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();

        try
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE categories SET name = @name WHERE id = @id";
                cmd.Parameters.Add(new SqliteParameter("@name", newName));
                cmd.Parameters.Add(new SqliteParameter("@id", id));
                var affected = await cmd.ExecuteNonQueryAsync(ct);
                if (affected == 0)
                {
                    tx.Rollback();
                    return null;
                }
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        var byId = await LoadAllRawAsync(connection, ct);
        if (!byId.TryGetValue(id, out var raw))
            return null;

        var fullPath = BuildFullPath(byId, raw.Id);
        return new CategoryNode(raw.Id, raw.Name, raw.ParentId, raw.SortOrder, fullPath, raw.CreatedAt);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();

        try
        {
            var descendantIds = await CollectDescendantIdsAsync(connection, id, ct);
            descendantIds.Add(id);

            foreach (var descId in descendantIds)
            {
                using var resetCmd = connection.CreateCommand();
                resetCmd.CommandText = "UPDATE catalog_items SET category_id = NULL WHERE category_id = @catId";
                resetCmd.Parameters.Add(new SqliteParameter("@catId", descId));
                await resetCmd.ExecuteNonQueryAsync(ct);
            }

            using var delCmd = connection.CreateCommand();
            delCmd.CommandText = "DELETE FROM categories WHERE id = @id";
            delCmd.Parameters.Add(new SqliteParameter("@id", id));
            var affected = await delCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            return affected > 0;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<CategoryNode?> MoveAsync(string id, string? newParentId, int sortOrder, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();

        try
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE categories SET parent_id = @parentId, sort_order = @sortOrder WHERE id = @id";
                cmd.Parameters.Add(new SqliteParameter("@parentId", (object?)newParentId ?? DBNull.Value));
                cmd.Parameters.Add(new SqliteParameter("@sortOrder", sortOrder));
                cmd.Parameters.Add(new SqliteParameter("@id", id));
                var affected = await cmd.ExecuteNonQueryAsync(ct);
                if (affected == 0)
                {
                    tx.Rollback();
                    return null;
                }
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        var byId = await LoadAllRawAsync(connection, ct);
        if (!byId.TryGetValue(id, out var raw))
            return null;

        var fullPath = BuildFullPath(byId, raw.Id);
        return new CategoryNode(raw.Id, raw.Name, newParentId, sortOrder, fullPath, raw.CreatedAt);
    }

    public async Task<int> GetFamilyCountAsync(string categoryId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM catalog_items WHERE category_id = @categoryId";
        cmd.Parameters.Add(new SqliteParameter("@categoryId", categoryId));

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? (int)l : 0;
    }

    public async Task ReorderAsync(IReadOnlyList<(string Id, int SortOrder)> items, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();

        try
        {
            foreach (var (itemId, sortOrder) in items)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "UPDATE categories SET sort_order = @sortOrder WHERE id = @id";
                cmd.Parameters.Add(new SqliteParameter("@sortOrder", sortOrder));
                cmd.Parameters.Add(new SqliteParameter("@id", itemId));
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

    public async Task ReplaceAllAsync(IReadOnlyList<CategoryNode> categories, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();

        try
        {
            using (var delCmd = connection.CreateCommand())
            {
                delCmd.CommandText = "DELETE FROM categories";
                await delCmd.ExecuteNonQueryAsync(ct);
            }

            foreach (var cat in categories)
            {
                using var insertCmd = connection.CreateCommand();
                insertCmd.CommandText = "INSERT INTO categories (id, name, parent_id, sort_order, created_at_utc) VALUES (@id, @name, @parentId, @sortOrder, @createdAt)";
                insertCmd.Parameters.Add(new SqliteParameter("@id", cat.Id));
                insertCmd.Parameters.Add(new SqliteParameter("@name", cat.Name));
                insertCmd.Parameters.Add(new SqliteParameter("@parentId", (object?)cat.ParentId ?? DBNull.Value));
                insertCmd.Parameters.Add(new SqliteParameter("@sortOrder", cat.SortOrder));
                insertCmd.Parameters.Add(new SqliteParameter("@createdAt", cat.CreatedAtUtc.ToString("o")));
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

    private static async Task<RawCategory?> ReadRawByIdAsync(SqliteConnection connection, string id, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, parent_id, sort_order, created_at_utc FROM categories WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new RawCategory(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt32(3),
            DateTimeOffset.Parse(reader.GetString(4)));
    }

    private static async Task<Dictionary<string, RawCategory>> LoadAllRawAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, parent_id, sort_order, created_at_utc FROM categories";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new Dictionary<string, RawCategory>();
        while (await reader.ReadAsync(ct))
        {
            var raw = new RawCategory(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3),
                DateTimeOffset.Parse(reader.GetString(4)));
            result[raw.Id] = raw;
        }
        return result;
    }

    private static string BuildFullPath(Dictionary<string, RawCategory> byId, string id)
    {
        var parts = new Stack<string>();
        var current = id;
        while (current is not null && byId.TryGetValue(current, out var node))
        {
            parts.Push(node.Name);
            current = node.ParentId;
        }
        return string.Join(" > ", parts);
    }

    private static async Task<List<string>> CollectDescendantIdsAsync(SqliteConnection connection, string parentId, CancellationToken ct)
    {
        var result = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(parentId);

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id FROM categories WHERE parent_id = @parentId";
            cmd.Parameters.Add(new SqliteParameter("@parentId", currentId));
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var childId = reader.GetString(0);
                result.Add(childId);
                queue.Enqueue(childId);
            }
        }

        return result;
    }

    private sealed record RawCategory(string Id, string Name, string? ParentId, int SortOrder, DateTimeOffset CreatedAt);

    public async Task<IReadOnlyDictionary<string, int>> GetAllFamilyCountsAsync(CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT category_id, COUNT(*) FROM catalog_items WHERE category_id IS NOT NULL GROUP BY category_id";

        var result = new Dictionary<string, int>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetString(0)] = (int)reader.GetInt64(1);
        }
        return result;
    }
}
