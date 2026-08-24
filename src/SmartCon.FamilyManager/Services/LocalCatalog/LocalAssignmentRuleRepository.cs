using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

/// <summary>
/// Local SQLite implementation of <see cref="IAssignmentRuleRepository"/>
/// (#241): OR-groups in <c>category_assignment_rule_groups</c>, AND-
/// conditions in <c>category_assignment_conditions</c>. Rows with unknown
/// operator / source kind / system key are skipped with a warning —
/// forward-compat with newer plugin versions (pattern of
/// LocalValidationRuleRepository).
/// </summary>
internal sealed class LocalAssignmentRuleRepository : IAssignmentRuleRepository
{
    private const string GroupColumns = "id, category_id, sort_order, is_enabled";
    private const string ConditionColumns = "id, group_id, source_kind, attribute_id, system_key, operator, value_text, value_number, min_value, max_value, sort_order, is_enabled";

    private readonly LocalCatalogDatabase _database;
    private readonly ILocalCatalogMigrator _migrator;
    private string? _migratedDbPath;

    public LocalAssignmentRuleRepository(LocalCatalogDatabase database, ILocalCatalogMigrator migrator)
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

    public async Task<IReadOnlyList<AssignmentRuleGroup>> GetGroupsWithConditionsAsync(CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        var groups = new List<AssignmentRuleGroup>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT {GroupColumns} FROM category_assignment_rule_groups ORDER BY category_id, sort_order";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                groups.Add(ReadGroup(reader));
            }
        }

        if (groups.Count == 0)
        {
            return groups.AsReadOnly();
        }

        var conditionsByGroup = await LoadConditionsAsync(connection, ct);
        return groups
            .Select(g => g with { Conditions = conditionsByGroup.TryGetValue(g.Id, out var list) ? list : [] })
            .ToList()
            .AsReadOnly();
    }

    public async Task<IReadOnlyList<AssignmentRuleGroup>> GetGroupsForCategoryAsync(string categoryId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        var groups = new List<AssignmentRuleGroup>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT {GroupColumns} FROM category_assignment_rule_groups WHERE category_id = @categoryId ORDER BY sort_order";
            cmd.Parameters.Add(new SqliteParameter("@categoryId", categoryId));
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                groups.Add(ReadGroup(reader));
            }
        }

        if (groups.Count == 0)
        {
            return groups.AsReadOnly();
        }

        var conditionsByGroup = await LoadConditionsAsync(connection, ct);
        return groups
            .Select(g => g with { Conditions = conditionsByGroup.TryGetValue(g.Id, out var list) ? list : [] })
            .ToList()
            .AsReadOnly();
    }

    public async Task<IReadOnlyDictionary<string, int>> GetEnabledGroupCountsAsync(CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT category_id, COUNT(*) FROM category_assignment_rule_groups WHERE is_enabled != 0 GROUP BY category_id";

        var result = new Dictionary<string, int>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetString(0)] = reader.GetInt32(1);
        }

        return result;
    }

    public async Task<AssignmentRuleGroup> CreateGroupAsync(string categoryId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        var id = Guid.NewGuid().ToString();

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        var nextSortOrder = await GetNextGroupSortOrderAsync(connection, categoryId, ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO category_assignment_rule_groups (id, category_id, sort_order, is_enabled) VALUES (@id, @categoryId, @sortOrder, 1)";
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@categoryId", categoryId));
        cmd.Parameters.Add(new SqliteParameter("@sortOrder", nextSortOrder));
        await cmd.ExecuteNonQueryAsync(ct);

        return new AssignmentRuleGroup(id, categoryId, nextSortOrder, true, []);
    }

    public async Task<bool> UpdateGroupAsync(string groupId, int? sortOrder, bool? isEnabled, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);

        var sets = new List<string>();
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();

        if (sortOrder.HasValue)
        {
            sets.Add("sort_order = @sortOrder");
            cmd.Parameters.Add(new SqliteParameter("@sortOrder", sortOrder.Value));
        }

        if (isEnabled.HasValue)
        {
            sets.Add("is_enabled = @isEnabled");
            cmd.Parameters.Add(new SqliteParameter("@isEnabled", isEnabled.Value ? 1 : 0));
        }

        if (sets.Count == 0)
        {
            return await GroupExistsAsync(connection, groupId, ct);
        }

        cmd.CommandText = $"UPDATE category_assignment_rule_groups SET {string.Join(", ", sets)} WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", groupId));
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    private static async Task<bool> GroupExistsAsync(SqliteConnection connection, string groupId, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM category_assignment_rule_groups WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", groupId));
        var scalar = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    public async Task<bool> DeleteGroupAsync(string groupId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM category_assignment_rule_groups WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", groupId));
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    public async Task<AssignmentCondition> CreateConditionAsync(
        string groupId,
        AssignmentConditionSourceKind sourceKind,
        string? attributeId,
        AssignmentSystemField? systemField,
        ValidationRuleOperator op,
        string? valueText,
        double? valueNumber,
        double? minValue,
        double? maxValue,
        bool isEnabled,
        CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        var id = Guid.NewGuid().ToString();

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        var nextSortOrder = await GetNextConditionSortOrderAsync(connection, groupId, ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "INSERT INTO category_assignment_conditions (id, group_id, source_kind, attribute_id, system_key, operator, value_text, value_number, min_value, max_value, sort_order, is_enabled) " +
            "VALUES (@id, @groupId, @sourceKind, @attributeId, @systemKey, @operator, @valueText, @valueNumber, @minValue, @maxValue, @sortOrder, @isEnabled)";
        FillConditionParameters(cmd, new AssignmentCondition(
            id, groupId, sourceKind, attributeId, systemField, op,
            valueText, valueNumber, minValue, maxValue, nextSortOrder, isEnabled));
        await cmd.ExecuteNonQueryAsync(ct);

        return new AssignmentCondition(
            id, groupId, sourceKind, attributeId, systemField, op,
            valueText, valueNumber, minValue, maxValue, nextSortOrder, isEnabled);
    }

    public async Task<bool> UpdateConditionAsync(AssignmentCondition condition, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "UPDATE category_assignment_conditions SET source_kind = @sourceKind, attribute_id = @attributeId, system_key = @systemKey, " +
            "operator = @operator, value_text = @valueText, value_number = @valueNumber, min_value = @minValue, max_value = @maxValue, " +
            "sort_order = @sortOrder, is_enabled = @isEnabled " +
            "WHERE id = @id";
        FillConditionParameters(cmd, condition);
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    public async Task<bool> DeleteConditionAsync(string conditionId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM category_assignment_conditions WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", conditionId));
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<AssignmentCondition>>> LoadConditionsAsync(
        SqliteConnection connection, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<AssignmentCondition>>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {ConditionColumns} FROM category_assignment_conditions ORDER BY group_id, sort_order";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var condition = TryReadCondition(reader);
            if (condition is null)
            {
                continue;
            }

            if (!result.TryGetValue(condition.GroupId, out var list))
            {
                list = new List<AssignmentCondition>();
                result[condition.GroupId] = list;
            }

            ((List<AssignmentCondition>)list).Add(condition);
        }

        return result;
    }

    private static async Task<int> GetNextGroupSortOrderAsync(SqliteConnection connection, string categoryId, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM category_assignment_rule_groups WHERE category_id = @categoryId";
        cmd.Parameters.Add(new SqliteParameter("@categoryId", categoryId));
        var scalar = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> GetNextConditionSortOrderAsync(SqliteConnection connection, string groupId, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM category_assignment_conditions WHERE group_id = @groupId";
        cmd.Parameters.Add(new SqliteParameter("@groupId", groupId));
        var scalar = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void FillConditionParameters(SqliteCommand cmd, AssignmentCondition condition)
    {
        cmd.Parameters.Add(new SqliteParameter("@id", condition.Id));
        cmd.Parameters.Add(new SqliteParameter("@groupId", condition.GroupId));
        cmd.Parameters.Add(new SqliteParameter("@sourceKind", ToStorageKind(condition.SourceKind)));
        cmd.Parameters.Add(new SqliteParameter("@attributeId", (object?)condition.AttributeId ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@systemKey", condition.SystemField.HasValue ? condition.SystemField.Value.ToString() : DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@operator", condition.Operator.ToString()));
        cmd.Parameters.Add(new SqliteParameter("@valueText", (object?)condition.ValueText ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@valueNumber", (object?)condition.ValueNumber ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@minValue", (object?)condition.MinValue ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@maxValue", (object?)condition.MaxValue ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@sortOrder", condition.SortOrder));
        cmd.Parameters.Add(new SqliteParameter("@isEnabled", condition.IsEnabled ? 1 : 0));
    }

    /// <summary>Storage form of the source kind — must match the CHECK
    /// constraint literals ('attribute' / 'system').</summary>
    private static string ToStorageKind(AssignmentConditionSourceKind sourceKind) =>
        sourceKind == AssignmentConditionSourceKind.System ? "system" : "attribute";

    private static AssignmentRuleGroup ReadGroup(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt32(3) != 0,
            []);

    private static AssignmentCondition? TryReadCondition(SqliteDataReader reader)
    {
        var id = reader.GetString(0);
        var groupId = reader.GetString(1);
        var sourceKindName = reader.GetString(2);

        if (!Enum.TryParse(sourceKindName, ignoreCase: true, out AssignmentConditionSourceKind sourceKind)
            || !Enum.IsDefined(typeof(AssignmentConditionSourceKind), sourceKind))
        {
            WarnUnknown("source kind", sourceKindName, id);
            return null;
        }

        string? attributeId = reader.IsDBNull(3) ? null : reader.GetString(3);
        AssignmentSystemField? systemField = null;
        if (!reader.IsDBNull(4))
        {
            var systemKeyName = reader.GetString(4);
            if (!Enum.TryParse(systemKeyName, ignoreCase: true, out AssignmentSystemField parsed)
                || !Enum.IsDefined(typeof(AssignmentSystemField), parsed))
            {
                WarnUnknown("system field", systemKeyName, id);
                return null;
            }

            systemField = parsed;
        }

        var operatorName = reader.GetString(5);
        if (!Enum.TryParse(operatorName, ignoreCase: true, out ValidationRuleOperator ruleOperator)
            || !Enum.IsDefined(typeof(ValidationRuleOperator), ruleOperator))
        {
            WarnUnknown("operator", operatorName, id);
            return null;
        }

        if (!AssignmentOperatorPolicy.IsAllowed(sourceKind, systemField, ruleOperator))
        {
            SmartConLogger.Warn(
                $"category_assignment_conditions: operator '{operatorName}' is not allowed for condition {id} — condition skipped " +
                "[Action: пересоздайте условие в редакторе правил автоназначения]");
            return null;
        }

        return new AssignmentCondition(
            id,
            groupId,
            sourceKind,
            attributeId,
            systemField,
            ruleOperator,
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetDouble(7),
            reader.IsDBNull(8) ? null : reader.GetDouble(8),
            reader.IsDBNull(9) ? null : reader.GetDouble(9),
            reader.GetInt32(10),
            reader.GetInt32(11) != 0);
    }

    private static void WarnUnknown(string what, string value, string conditionId) =>
        SmartConLogger.Warn(
            $"category_assignment_conditions: unknown {what} '{value}' in condition {conditionId} — condition skipped " +
            "[Action: обновите плагин до версии, поддерживающей это значение, или пересоздайте условие в редакторе правил автоназначения]");
}
