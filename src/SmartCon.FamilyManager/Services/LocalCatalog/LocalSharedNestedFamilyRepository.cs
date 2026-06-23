using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

/// <summary>
/// SQLite-backed implementation of <see cref="ISharedNestedFamilyRepository"/>.
/// Persists the names of shared nested families declared by each catalog
/// version, so the load path can render the correct name in the SmartCon
/// "Shared nested family — loading mode" dialog when the Revit API returns
/// <c>null</c> for the nested family reference (REVIT-198137 in Revit
/// 2023 and Revit 2024 prior to 24.3.0.13).
/// </summary>
internal sealed class LocalSharedNestedFamilyRepository : ISharedNestedFamilyRepository
{
    private readonly LocalCatalogDatabase _database;

    public LocalSharedNestedFamilyRepository(LocalCatalogDatabase database)
    {
        _database = database;
    }

    public async Task ReplaceForVersionAsync(
        string catalogItemId,
        string versionId,
        IReadOnlyList<string> nestedSharedNames,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(catalogItemId))
            throw new ArgumentException("catalogItemId is required", nameof(catalogItemId));
        if (string.IsNullOrEmpty(versionId))
            throw new ArgumentException("versionId is required", nameof(versionId));

        using var _scope = SmartConLogger.BeginScope("NestedSharedRepo",
            ("Method", "ReplaceForVersionAsync"),
            ("CatalogItemId", catalogItemId),
            ("VersionId", versionId),
            ("Count", nestedSharedNames.Count));

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();
        try
        {
            using (var del = connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM family_nested_shared_families WHERE version_id = @versionId";
                del.Parameters.Add(new SqliteParameter("@versionId", versionId));
                await del.ExecuteNonQueryAsync(ct);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordinal = 0;
            foreach (var raw in nestedSharedNames)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (!seen.Add(raw)) continue;

                using var ins = connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT OR IGNORE INTO family_nested_shared_families
                        (catalog_item_id, version_id, nested_family_name, ordinal)
                    VALUES (@item, @version, @name, @ord)
                    """;
                ins.Parameters.Add(new SqliteParameter("@item", catalogItemId));
                ins.Parameters.Add(new SqliteParameter("@version", versionId));
                ins.Parameters.Add(new SqliteParameter("@name", raw));
                ins.Parameters.Add(new SqliteParameter("@ord", ordinal));
                await ins.ExecuteNonQueryAsync(ct);
                ordinal++;
            }

            tx.Commit();
            SmartConLogger.Info($"Persisted {ordinal} unique nested names (input={nestedSharedNames.Count})");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<IReadOnlyList<string>> GetNamesForCurrentVersionAsync(
        string catalogItemId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(catalogItemId))
            throw new ArgumentException("catalogItemId is required", nameof(catalogItemId));

        using var _scope = SmartConLogger.BeginScope("NestedSharedRepo",
            ("Method", "GetNamesForCurrentVersionAsync"),
            ("CatalogItemId", catalogItemId));

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT nsf.nested_family_name
            FROM family_nested_shared_families nsf
            JOIN catalog_versions cv ON cv.id = nsf.version_id
            INNER JOIN catalog_items ci
                ON ci.id = cv.catalog_item_id
               AND ci.current_version_label = cv.version_label
            WHERE nsf.catalog_item_id = @itemId
            ORDER BY nsf.ordinal
            """;
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));

        var result = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(reader.GetString(0));
        }

        SmartConLogger.Info($"Loaded {result.Count} nested names from catalog DB");
        return result;
    }
}
