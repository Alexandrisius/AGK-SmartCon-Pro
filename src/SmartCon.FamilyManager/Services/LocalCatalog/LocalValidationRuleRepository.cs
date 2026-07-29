using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class LocalValidationRuleRepository : IValidationRuleRepository
{
    private readonly LocalCatalogDatabase _database;
    private readonly ILocalCatalogMigrator _migrator;
    private string? _migratedDbPath;

    public LocalValidationRuleRepository(LocalCatalogDatabase database, ILocalCatalogMigrator migrator)
    {
        _database = database;
        _migrator = migrator;
    }

    private async Task EnsureMigratedAsync(CancellationToken ct)
    {
        var currentPath = _database.GetDatabaseRoot();
        if (_migratedDbPath == currentPath) return;
        await _migrator.MigrateAsync(ct);
        _migratedDbPath = currentPath;
    }

    public async Task<IReadOnlyList<ValidationRule>> GetRulesForBindingAsync(string bindingId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        var result = new List<ValidationRule>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, binding_id, operator, value_text, value_number, min_value, max_value, unit_type_id, sort_order, is_enabled FROM category_validation_rules WHERE binding_id = @bindingId ORDER BY sort_order";
        cmd.Parameters.Add(new SqliteParameter("@bindingId", bindingId));

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var rule = TryReadRule(reader);
            if (rule is not null)
            {
                result.Add(rule);
            }
        }

        return result.AsReadOnly();
    }

    public async Task<IReadOnlyList<ValidationRule>> GetRulesForBindingsAsync(IEnumerable<string> bindingIds, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        var idList = bindingIds.ToList();
        if (idList.Count == 0)
            return Array.Empty<ValidationRule>();

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();

        var placeholders = string.Join(", ", idList.Select((_, i) => $"@p{i}"));
        cmd.CommandText = $"SELECT id, binding_id, operator, value_text, value_number, min_value, max_value, unit_type_id, sort_order, is_enabled FROM category_validation_rules WHERE binding_id IN ({placeholders}) ORDER BY binding_id, sort_order";
        for (var i = 0; i < idList.Count; i++)
        {
            cmd.Parameters.Add(new SqliteParameter($"@p{i}", idList[i]));
        }

        var result = new List<ValidationRule>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var rule = TryReadRule(reader);
            if (rule is not null)
            {
                result.Add(rule);
            }
        }

        return result.AsReadOnly();
    }

    public async Task<IReadOnlyDictionary<string, ValidationRuleCounts>> GetRuleCountsForBindingsAsync(IEnumerable<string> bindingIds, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        var idList = bindingIds.ToList();
        if (idList.Count == 0)
            return new Dictionary<string, ValidationRuleCounts>();

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();

        var placeholders = string.Join(", ", idList.Select((_, i) => $"@p{i}"));
        cmd.CommandText = $"SELECT binding_id, COUNT(*) as cnt, SUM(CASE WHEN is_enabled = 0 THEN 1 ELSE 0 END) as disabled_cnt FROM category_validation_rules WHERE binding_id IN ({placeholders}) GROUP BY binding_id";
        for (var i = 0; i < idList.Count; i++)
        {
            cmd.Parameters.Add(new SqliteParameter($"@p{i}", idList[i]));
        }

        var result = new Dictionary<string, ValidationRuleCounts>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetString(0)] = new ValidationRuleCounts(reader.GetInt32(1), reader.GetInt32(2));
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<string, int>> GetRuleCountsForAttributesAsync(IEnumerable<string> attributeIds, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        var idList = attributeIds.ToList();
        if (idList.Count == 0)
            return new Dictionary<string, int>();

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();

        var placeholders = string.Join(", ", idList.Select((_, i) => $"@p{i}"));
        cmd.CommandText = $"SELECT cab.attribute_id, COUNT(*) as cnt FROM category_validation_rules cvr JOIN category_attribute_bindings cab ON cvr.binding_id = cab.id WHERE cab.attribute_id IN ({placeholders}) GROUP BY cab.attribute_id";
        for (var i = 0; i < idList.Count; i++)
        {
            cmd.Parameters.Add(new SqliteParameter($"@p{i}", idList[i]));
        }

        var result = new Dictionary<string, int>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetString(0)] = reader.GetInt32(1);
        }

        return result;
    }

    public async Task<ValidationRule> CreateRuleAsync(ValidationRule rule, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        var id = string.IsNullOrEmpty(rule.Id) ? Guid.NewGuid().ToString() : rule.Id;

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO category_validation_rules (id, binding_id, operator, value_text, value_number, min_value, max_value, unit_type_id, sort_order, is_enabled) VALUES (@id, @bindingId, @operator, @valueText, @valueNumber, @minValue, @maxValue, @unitTypeId, @sortOrder, @isEnabled)";
        FillParameters(cmd, id, rule);
        await cmd.ExecuteNonQueryAsync(ct);

        return rule with { Id = id };
    }

    public async Task<ValidationRule> UpdateRuleAsync(ValidationRule rule, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE category_validation_rules SET binding_id = @bindingId, operator = @operator, value_text = @valueText, value_number = @valueNumber, min_value = @minValue, max_value = @maxValue, unit_type_id = @unitTypeId, sort_order = @sortOrder, is_enabled = @isEnabled WHERE id = @id";
        FillParameters(cmd, rule.Id, rule);
        await cmd.ExecuteNonQueryAsync(ct);

        return rule;
    }

    public async Task<bool> DeleteRuleAsync(string ruleId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM category_validation_rules WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", ruleId));
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    public async Task DeleteRulesForBindingAsync(string bindingId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM category_validation_rules WHERE binding_id = @bindingId";
        cmd.Parameters.Add(new SqliteParameter("@bindingId", bindingId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void FillParameters(SqliteCommand cmd, string id, ValidationRule rule)
    {
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@bindingId", rule.BindingId));
        cmd.Parameters.Add(new SqliteParameter("@operator", rule.Operator.ToString()));
        cmd.Parameters.Add(new SqliteParameter("@valueText", (object?)rule.ValueText ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@valueNumber", (object?)rule.ValueNumber ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@minValue", (object?)rule.MinValue ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@maxValue", (object?)rule.MaxValue ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@unitTypeId", (object?)rule.UnitTypeId ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@sortOrder", rule.SortOrder));
        cmd.Parameters.Add(new SqliteParameter("@isEnabled", rule.IsEnabled ? 1 : 0));
    }

    private static ValidationRule? TryReadRule(SqliteDataReader reader)
    {
        var operatorName = reader.GetString(2);
        if (!Enum.TryParse<ValidationRuleOperator>(operatorName, out var ruleOperator)
            || !Enum.IsDefined(typeof(ValidationRuleOperator), ruleOperator))
        {
            SmartConLogger.Warn(
                $"category_validation_rules: unknown operator '{operatorName}' in rule {reader.GetString(0)} — rule skipped " +
                $"[Action: обновите плагин до версии, поддерживающей этот оператор, или пересоздайте правило в редакторе категорий]");
            return null;
        }

        return new ValidationRule(
            reader.GetString(0),
            reader.GetString(1),
            ruleOperator,
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetDouble(4),
            reader.IsDBNull(5) ? null : reader.GetDouble(5),
            reader.IsDBNull(6) ? null : reader.GetDouble(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt32(8),
            reader.GetInt32(9) != 0);
    }
}
