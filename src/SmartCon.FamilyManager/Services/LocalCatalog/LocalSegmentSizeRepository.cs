using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

/// <summary>
/// SQLite implementation of <see cref="ISegmentSizeRepository"/> (ADR-072,
/// Phase 3, V36): per-version segment size tables feeding the routing
/// editor's nominal-diameter dropdowns.
/// </summary>
internal sealed class LocalSegmentSizeRepository : ISegmentSizeRepository
{
    private readonly LocalCatalogDatabase _database;

    public LocalSegmentSizeRepository(LocalCatalogDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task ReplaceForVersionAsync(
        string catalogVersionId, IReadOnlyList<SegmentSizeRecord> sizes, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(catalogVersionId))
            throw new ArgumentException("catalogVersionId is required", nameof(catalogVersionId));

        using var _scope = SmartConLogger.BeginScope("SegmentSizeRepo",
            ("Method", nameof(ReplaceForVersionAsync)),
            ("VersionId", catalogVersionId),
            ("Count", sizes.Count));

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var tx = connection.BeginTransaction();
        try
        {
            using (var del = connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM family_segment_sizes WHERE catalog_version_id = @versionId";
                del.Parameters.Add(new SqliteParameter("@versionId", catalogVersionId));
                await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            foreach (var size in sizes)
            {
                ct.ThrowIfCancellationRequested();
                using var ins = connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT OR REPLACE INTO family_segment_sizes
                        (catalog_version_id, segment_name, nominal_diameter, inner_diameter,
                         outer_diameter, used_in_size_lists, used_in_sizing, sort_order)
                    VALUES (@version, @segment, @nominal, @inner, @outer, @lists, @sizing, @order)
                    """;
                ins.Parameters.Add(new SqliteParameter("@version", catalogVersionId));
                ins.Parameters.Add(new SqliteParameter("@segment", size.SegmentName));
                ins.Parameters.Add(new SqliteParameter("@nominal", size.NominalDiameter));
                ins.Parameters.Add(new SqliteParameter("@inner", size.InnerDiameter));
                ins.Parameters.Add(new SqliteParameter("@outer", size.OuterDiameter));
                ins.Parameters.Add(new SqliteParameter("@lists", size.UsedInSizeLists ? 1 : 0));
                ins.Parameters.Add(new SqliteParameter("@sizing", size.UsedInSizing ? 1 : 0));
                ins.Parameters.Add(new SqliteParameter("@order", size.SortOrder));
                await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            tx.Commit();
            SmartConLogger.Info($"Persisted {sizes.Count} segment size rows");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task ReplaceForCurrentVersionAsync(
        string catalogItemId, IReadOnlyList<SegmentSizeRecord> sizes, CancellationToken ct = default)
    {
        var versionId = await ResolveCurrentVersionIdAsync(catalogItemId, ct).ConfigureAwait(false);
        if (versionId is null)
        {
            SmartConLogger.Warn(
                $"Segment sizes not written: item {catalogItemId} has no current version. " +
                "[Action: повторите импорт родителя — размеры запишутся для его активной версии]");
            return;
        }
        await ReplaceForVersionAsync(versionId, sizes, ct).ConfigureAwait(false);
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
        return (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) as string;
    }

    public async Task<IReadOnlyList<SegmentSizeRecord>> ReadForVersionAsync(
        string catalogVersionId, CancellationToken ct = default)
    {
        var result = new List<SegmentSizeRecord>();
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT segment_name, nominal_diameter, inner_diameter, outer_diameter,
                   used_in_size_lists, used_in_sizing, sort_order
            FROM family_segment_sizes
            WHERE catalog_version_id = @versionId
            ORDER BY segment_name, nominal_diameter
            """;
        cmd.Parameters.Add(new SqliteParameter("@versionId", catalogVersionId));
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new SegmentSizeRecord(
                reader.GetString(0),
                reader.GetDouble(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetInt32(4) != 0,
                reader.GetInt32(5) != 0,
                reader.GetInt32(6)));
        }
        return result;
    }

    public async Task<IReadOnlyList<double>> ReadDistinctNominalsAsync(
        string catalogVersionId, CancellationToken ct = default)
    {
        var result = new List<double>();
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT nominal_diameter
            FROM family_segment_sizes
            WHERE catalog_version_id = @versionId AND nominal_diameter > 0
            ORDER BY nominal_diameter
            """;
        cmd.Parameters.Add(new SqliteParameter("@versionId", catalogVersionId));
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(reader.GetDouble(0));
        }
        return result;
    }
}
