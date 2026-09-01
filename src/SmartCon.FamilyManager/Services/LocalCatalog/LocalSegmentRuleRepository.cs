using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

/// <summary>
/// SQLite implementation of <see cref="ISegmentRuleRepository"/> (FHV21,
/// V38): per-version segment routing rules of pipe types.
/// </summary>
internal sealed class LocalSegmentRuleRepository : ISegmentRuleRepository
{
    private readonly LocalCatalogDatabase _database;

    public LocalSegmentRuleRepository(LocalCatalogDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<IReadOnlyList<SegmentRuleRecord>> ReadForVersionAsync(
        string catalogVersionId, CancellationToken ct = default)
    {
        var result = new List<SegmentRuleRecord>();
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT type_name, family_key, rule_order, segment_name, min_size_feet, max_size_feet, description
            FROM family_segment_rules
            WHERE catalog_version_id = @versionId
            ORDER BY family_key, type_name, rule_order
            """;
        cmd.Parameters.Add(new SqliteParameter("@versionId", catalogVersionId));
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new SegmentRuleRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.GetString(6)));
        }
        return result;
    }

    public async Task<IReadOnlyList<SegmentRuleRecord>> ReadForCurrentVersionAsync(
        string catalogItemId, CancellationToken ct = default)
    {
        var versionId = await ResolveCurrentVersionIdAsync(catalogItemId, ct).ConfigureAwait(false);
        return versionId is null
            ? []
            : await ReadForVersionAsync(versionId, ct).ConfigureAwait(false);
    }

    public async Task ReplaceForCurrentVersionAsync(
        string catalogItemId, IReadOnlyList<SegmentRuleRecord> rules, CancellationToken ct = default)
    {
        var versionId = await ResolveCurrentVersionIdAsync(catalogItemId, ct).ConfigureAwait(false);
        if (versionId is null)
        {
            SmartConLogger.Warn(
                $"Segment rules not written: item {catalogItemId} has no current version. " +
                "[Action: повторите импорт родителя — настройка сегментов запишется для его активной версии]");
            return;
        }
        await ReplaceForVersionAsync(versionId, rules, ct).ConfigureAwait(false);
    }

    private async Task<string?> ResolveCurrentVersionIdAsync(string catalogItemId, CancellationToken ct)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT cv.id
            FROM catalog_versions cv
            INNER JOIN catalog_items ci ON ci.id = cv.catalog_item_id
            WHERE cv.catalog_item_id = @itemId AND cv.version_label = ci.current_version_label
            LIMIT 1
            """;
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as string;
    }

    public async Task ReplaceForVersionAsync(
        string catalogVersionId, IReadOnlyList<SegmentRuleRecord> rules, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(catalogVersionId))
            throw new ArgumentException("catalogVersionId is required", nameof(catalogVersionId));

        using var _scope = SmartConLogger.BeginScope("SegmentRuleRepo",
            ("Method", nameof(ReplaceForVersionAsync)),
            ("VersionId", catalogVersionId),
            ("Count", rules.Count));

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var tx = connection.BeginTransaction();
        try
        {
            using (var del = connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM family_segment_rules WHERE catalog_version_id = @versionId";
                del.Parameters.Add(new SqliteParameter("@versionId", catalogVersionId));
                await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            foreach (var rule in rules)
            {
                ct.ThrowIfCancellationRequested();
                using var ins = connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT OR REPLACE INTO family_segment_rules
                        (catalog_version_id, family_key, type_name, rule_order, segment_name,
                         min_size_feet, max_size_feet, description)
                    VALUES (@version, @familyKey, @typeName, @order, @segment, @min, @max, @description)
                    """;
                ins.Parameters.Add(new SqliteParameter("@version", catalogVersionId));
                ins.Parameters.Add(new SqliteParameter("@familyKey", rule.FamilyKey));
                ins.Parameters.Add(new SqliteParameter("@typeName", rule.TypeName));
                ins.Parameters.Add(new SqliteParameter("@order", rule.RuleOrder));
                ins.Parameters.Add(new SqliteParameter("@segment", rule.SegmentName));
                ins.Parameters.Add(new SqliteParameter("@min",
                    rule.MinSizeFeet is { } min ? min : (object)DBNull.Value));
                ins.Parameters.Add(new SqliteParameter("@max",
                    rule.MaxSizeFeet is { } max ? max : (object)DBNull.Value));
                ins.Parameters.Add(new SqliteParameter("@description", rule.Description));
                await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            tx.Commit();
            SmartConLogger.Info($"Persisted {rules.Count} segment rule row(s)");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}
