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

    public async Task ReplaceForCurrentVersionAsync(
        string catalogItemId,
        IReadOnlyList<FamilyRoutingRuleInfo> rules,
        IReadOnlyList<FamilyRoutingTypeSettings> settings,
        CancellationToken ct = default)
    {
        var versionId = await ResolveCurrentVersionIdAsync(catalogItemId, ct);
        if (versionId is null)
        {
            SmartConLogger.Warn(
                $"Routing rules not written: item {catalogItemId} has no current version. " +
                "[Action: повторите импорт родителя — правила запишутся для его активной версии]");
            return;
        }
        await ReplaceForVersionAsync(catalogItemId, versionId, rules, settings, ct);

        // ADR-072 Phase 2b (review minor-1): a version whose routing was
        // just written at import is backfilled AND slim by construction —
        // mark it so the optional backfill task never re-opens its file.
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE catalog_versions SET routing_backfilled = 1 WHERE id = @vid AND routing_backfilled = 0";
        cmd.Parameters.Add(new SqliteParameter("@vid", versionId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(IReadOnlyList<FamilyRoutingRuleInfo> Rules, IReadOnlyList<FamilyRoutingTypeSettings> Settings)>
        ReadForCurrentVersionAsync(
            string catalogItemId,
            CancellationToken ct = default)
    {
        var versionId = await ResolveCurrentVersionIdAsync(catalogItemId, ct);
        return versionId is null
            ? ((IReadOnlyList<FamilyRoutingRuleInfo>)Array.Empty<FamilyRoutingRuleInfo>(),
                (IReadOnlyList<FamilyRoutingTypeSettings>)Array.Empty<FamilyRoutingTypeSettings>())
            : await ReadForVersionAsync(catalogItemId, versionId, ct);
    }

    public async Task<bool> HasRulesForCurrentVersionAsync(
        string catalogItemId,
        CancellationToken ct = default)
    {
        var versionId = await ResolveCurrentVersionIdAsync(catalogItemId, ct);
        return versionId is not null && await HasRulesForVersionAsync(catalogItemId, versionId, ct);
    }

    private async Task<string?> ResolveCurrentVersionIdAsync(string catalogItemId, CancellationToken ct)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT cv.id
            FROM catalog_versions cv
            INNER JOIN catalog_items ci ON ci.id = cv.catalog_item_id
            WHERE cv.catalog_item_id = @itemId AND cv.version_label = ci.current_version_label
            LIMIT 1
            """;
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        return (await cmd.ExecuteScalarAsync(ct)) as string;
    }

    public async Task<bool> HasAnyForItemAsync(
        string catalogItemId,
        CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM item_routing_type_settings WHERE catalog_item_id = @itemId
                UNION ALL
                SELECT 1 FROM item_routing_rules WHERE catalog_item_id = @itemId
                LIMIT 1)
            """;
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long value && value != 0;
    }

    public async Task<(IReadOnlyList<FamilyRoutingRuleInfo> Rules, IReadOnlyList<FamilyRoutingTypeSettings> Settings)>
        ReadForItemAsync(
            string catalogItemId,
            CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        var rules = new List<FamilyRoutingRuleInfo>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT family_key, type_name, group_key, rule_order, part_name, description, criteria_json
                FROM item_routing_rules
                WHERE catalog_item_id = @itemId
                ORDER BY type_name, group_key, rule_order
                """;
            cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rules.Add(new FamilyRoutingRuleInfo(
                    reader.GetString(1),
                    reader.GetString(0),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5),
                    DeserializeCriteria(reader.GetString(6))));
            }
        }

        var settings = new List<FamilyRoutingTypeSettings>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT family_key, type_name, preferred_junction_type
                FROM item_routing_type_settings
                WHERE catalog_item_id = @itemId
                """;
            cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                settings.Add(new FamilyRoutingTypeSettings(
                    reader.GetString(1), reader.GetString(0), reader.GetInt32(2)));
            }
        }

        return (rules, settings);
    }

    public async Task ReplaceForItemAsync(
        string catalogItemId,
        IReadOnlyList<FamilyRoutingRuleInfo> rules,
        IReadOnlyList<FamilyRoutingTypeSettings> settings,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(catalogItemId))
            throw new ArgumentException("catalogItemId is required", nameof(catalogItemId));

        using var _scope = SmartConLogger.BeginScope("RoutingRuleRepo",
            ("Method", nameof(ReplaceForItemAsync)),
            ("CatalogItemId", catalogItemId),
            ("Count", rules.Count));

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();
        try
        {
            using (var del = connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM item_routing_rules WHERE catalog_item_id = @itemId";
                del.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                await del.ExecuteNonQueryAsync(ct);
                del.CommandText = "DELETE FROM item_routing_type_settings WHERE catalog_item_id = @itemId";
                await del.ExecuteNonQueryAsync(ct);
            }

            foreach (var rule in rules)
            {
                ct.ThrowIfCancellationRequested();
                using var ins = connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO item_routing_rules
                        (catalog_item_id, family_key, type_name, group_key, rule_order,
                         part_name, description, criteria_json)
                    VALUES (@item, @famKey, @type, @group, @order, @part, @descr, @criteria)
                    """;
                ins.Parameters.Add(new SqliteParameter("@item", catalogItemId));
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
                    INSERT INTO item_routing_type_settings
                        (catalog_item_id, family_key, type_name, preferred_junction_type)
                    VALUES (@item, @famKey, @type, @preferred)
                    """;
                ins.Parameters.Add(new SqliteParameter("@item", catalogItemId));
                ins.Parameters.Add(new SqliteParameter("@famKey", setting.FamilyKey));
                ins.Parameters.Add(new SqliteParameter("@type", setting.TypeName));
                ins.Parameters.Add(new SqliteParameter("@preferred", setting.PreferredJunctionType));
                await ins.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
            SmartConLogger.Info(
                $"Persisted {rules.Count} item routing rules + {settings.Count} type settings (in place, no version)");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task MarkCurrentVersionRoutingBackfilledAsync(
        string catalogItemId,
        CancellationToken ct = default)
    {
        var versionId = await ResolveCurrentVersionIdAsync(catalogItemId, ct);
        if (versionId is null)
            return;
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE catalog_versions SET routing_backfilled = 1 WHERE id = @vid AND routing_backfilled = 0";
        cmd.Parameters.Add(new SqliteParameter("@vid", versionId));
        await cmd.ExecuteNonQueryAsync(ct);
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
                $"Corrupt criteria_json in routing rules storage: {ex.Message} " +
                "[Action: правило прочитано без критериев размера; переимпортируйте системную категорию для восстановления]");
            return Array.Empty<RoutingCriterionSnapshot>();
        }
    }
    public async Task<IReadOnlyList<RoutingPartReference>> ReadAllPartReferencesAsync(CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        // The Segments group (ForManagerGroup(0)) is the segment
        // configuration, not a fitting reference — excluded like in
        // DependencyLinkWriter.AugmentLinksFromRoutingRulesAsync.
        cmd.CommandText = """
            SELECT catalog_item_id, family_key, type_name, part_name
            FROM item_routing_rules
            WHERE part_name IS NOT NULL
              AND group_key != @segmentsGroup
            ORDER BY catalog_item_id, type_name, group_key, rule_order
            """;
        cmd.Parameters.Add(new SqliteParameter("@segmentsGroup", RoutingGroupKeys.ForManagerGroup(0)));
        var references = new List<RoutingPartReference>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            references.Add(new RoutingPartReference(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }
        return references;
    }
}
