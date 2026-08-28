using System.Text.Json;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

/// <summary>
/// SQLite-backed implementation of <see cref="IFamilyRoutingRuleRepository"/>
/// (ADR-072, V34). Mirrors the <see cref="LocalFamilyDependencyRepository"/>
/// patterns: DELETE+INSERT per catalog version inside one transaction.
/// Criteria ride as a JSON array so arbitrary criterion kinds survive the
/// roundtrip untouched.
/// </summary>
internal sealed class LocalFamilyRoutingRuleRepository : IFamilyRoutingRuleRepository
{
    private readonly LocalCatalogDatabase _database;

    public LocalFamilyRoutingRuleRepository(LocalCatalogDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task ReplaceForVersionAsync(
        string catalogItemId,
        string catalogVersionId,
        IReadOnlyList<FamilyRoutingRuleInfo> rules,
        IReadOnlyList<FamilyRoutingTypeSettings> settings,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(catalogItemId))
            throw new ArgumentException("catalogItemId is required", nameof(catalogItemId));
        if (string.IsNullOrEmpty(catalogVersionId))
            throw new ArgumentException("catalogVersionId is required", nameof(catalogVersionId));

        using var _scope = SmartConLogger.BeginScope("RoutingRuleRepo",
            ("Method", nameof(ReplaceForVersionAsync)),
            ("CatalogItemId", catalogItemId),
            ("VersionId", catalogVersionId),
            ("Count", rules.Count));

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();
        try
        {
            using (var del = connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM family_routing_rules WHERE catalog_version_id = @versionId";
                del.Parameters.Add(new SqliteParameter("@versionId", catalogVersionId));
                await del.ExecuteNonQueryAsync(ct);
                del.CommandText = "DELETE FROM family_routing_type_settings WHERE catalog_version_id = @versionId";
                await del.ExecuteNonQueryAsync(ct);
            }

            foreach (var rule in rules)
            {
                ct.ThrowIfCancellationRequested();
                using var ins = connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO family_routing_rules
                        (catalog_item_id, catalog_version_id, family_key, type_name,
                         group_key, rule_order, part_name, description, criteria_json)
                    VALUES (@item, @version, @famKey, @type, @group, @order, @part, @descr, @criteria)
                    """;
                ins.Parameters.Add(new SqliteParameter("@item", catalogItemId));
                ins.Parameters.Add(new SqliteParameter("@version", catalogVersionId));
                ins.Parameters.Add(new SqliteParameter("@famKey", rule.FamilyKey));
                ins.Parameters.Add(new SqliteParameter("@type", rule.TypeName));
                ins.Parameters.Add(new SqliteParameter("@group", rule.GroupKey));
                ins.Parameters.Add(new SqliteParameter("@order", rule.RuleOrder));
                ins.Parameters.Add(new SqliteParameter("@part", (object?)rule.PartName ?? DBNull.Value));
                ins.Parameters.Add(new SqliteParameter("@descr", rule.Description));
                ins.Parameters.Add(new SqliteParameter("@criteria", JsonSerializer.Serialize(rule.Criteria)));
                await ins.ExecuteNonQueryAsync(ct);
            }

            foreach (var setting in settings)
            {
                ct.ThrowIfCancellationRequested();
                using var ins = connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO family_routing_type_settings
                        (catalog_version_id, family_key, type_name, preferred_junction_type)
                    VALUES (@version, @famKey, @type, @preferred)
                    """;
                ins.Parameters.Add(new SqliteParameter("@version", catalogVersionId));
                ins.Parameters.Add(new SqliteParameter("@famKey", setting.FamilyKey));
                ins.Parameters.Add(new SqliteParameter("@type", setting.TypeName));
                ins.Parameters.Add(new SqliteParameter("@preferred", setting.PreferredJunctionType));
                await ins.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
            SmartConLogger.Info(
                $"Persisted {rules.Count} routing rules + {settings.Count} type settings");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<(IReadOnlyList<FamilyRoutingRuleInfo> Rules, IReadOnlyList<FamilyRoutingTypeSettings> Settings)>
        ReadForVersionAsync(
            string catalogItemId,
            string catalogVersionId,
            CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        var rules = new List<FamilyRoutingRuleInfo>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT family_key, type_name, group_key, rule_order, part_name, description, criteria_json
                FROM family_routing_rules
                WHERE catalog_version_id = @versionId
                ORDER BY type_name, group_key, rule_order
                """;
            cmd.Parameters.Add(new SqliteParameter("@versionId", catalogVersionId));
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var criteriaJson = reader.GetString(6);
                var criteria = DeserializeCriteria(criteriaJson);
                rules.Add(new FamilyRoutingRuleInfo(
                    reader.GetString(1),
                    reader.GetString(0),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5),
                    criteria));
            }
        }

        var settings = new List<FamilyRoutingTypeSettings>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT family_key, type_name, preferred_junction_type
                FROM family_routing_type_settings
                WHERE catalog_version_id = @versionId
                """;
            cmd.Parameters.Add(new SqliteParameter("@versionId", catalogVersionId));
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                settings.Add(new FamilyRoutingTypeSettings(
                    reader.GetString(1), reader.GetString(0), reader.GetInt32(2)));
            }
        }

        return (rules, settings);
    }

    public async Task<bool> HasRulesForVersionAsync(
        string catalogItemId,
        string catalogVersionId,
        CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM family_routing_type_settings WHERE catalog_version_id = @versionId
                UNION ALL
                SELECT 1 FROM family_routing_rules WHERE catalog_version_id = @versionId
                LIMIT 1)
            """;
        cmd.Parameters.Add(new SqliteParameter("@versionId", catalogVersionId));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long value && value != 0;
    }

    private static IReadOnlyList<RoutingCriterionSnapshot> DeserializeCriteria(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<RoutingCriterionSnapshot>>(json)
                ?? (IReadOnlyList<RoutingCriterionSnapshot>)Array.Empty<RoutingCriterionSnapshot>();
        }
        catch (JsonException ex)
        {
            SmartConLogger.Warn(
                $"Corrupt criteria_json in family_routing_rules: {ex.Message} " +
                "[Action: правило прочитано без критериев размера; переимпортируйте системную категорию для восстановления]");
            return Array.Empty<RoutingCriterionSnapshot>();
        }
    }
}
