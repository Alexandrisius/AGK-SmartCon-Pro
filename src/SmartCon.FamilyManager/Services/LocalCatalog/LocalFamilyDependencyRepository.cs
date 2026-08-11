using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

/// <summary>
/// SQLite-backed implementation of <see cref="IFamilyDependencyRepository"/>
/// (ADR-066). Mirrors the <see cref="LocalSharedNestedFamilyRepository"/>
/// patterns: DELETE+INSERT per parent version inside one transaction,
/// application-layer dedup (SQLite NOCASE does not fold Cyrillic).
/// </summary>
internal sealed class LocalFamilyDependencyRepository : IFamilyDependencyRepository
{
    private readonly LocalCatalogDatabase _database;

    public LocalFamilyDependencyRepository(LocalCatalogDatabase database)
    {
        _database = database;
    }

    public async Task ReplaceForVersionAsync(
        string parentCatalogItemId,
        string parentVersionId,
        IReadOnlyList<FamilyDependencyInfo> dependencies,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(parentCatalogItemId))
            throw new ArgumentException("parentCatalogItemId is required", nameof(parentCatalogItemId));
        if (string.IsNullOrEmpty(parentVersionId))
            throw new ArgumentException("parentVersionId is required", nameof(parentVersionId));

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        await ReplaceForVersionCoreAsync(
            connection, parentCatalogItemId, parentVersionId, dependencies, ct);
    }

    private static async Task<int> ReplaceForVersionCoreAsync(
        SqliteConnection connection,
        string parentCatalogItemId,
        string parentVersionId,
        IReadOnlyList<FamilyDependencyInfo> dependencies,
        CancellationToken ct)
    {
        using var _scope = SmartConLogger.BeginScope("FamilyDepRepo",
            ("Method", "ReplaceForVersion"),
            ("CatalogItemId", parentCatalogItemId),
            ("VersionId", parentVersionId),
            ("Count", dependencies.Count));

        using var tx = connection.BeginTransaction();
        try
        {
            using (var del = connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM family_dependencies WHERE parent_version_id = @versionId";
                del.Parameters.Add(new SqliteParameter("@versionId", parentVersionId));
                await del.ExecuteNonQueryAsync(ct);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordinal = 0;
            foreach (var dep in dependencies)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(dep.ChildCatalogItemId)) continue;
                if (string.IsNullOrWhiteSpace(dep.Kind)) continue;
                if (!seen.Add($"{dep.ChildCatalogItemId}|{dep.Kind}")) continue;

                using var ins = connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT OR IGNORE INTO family_dependencies
                        (parent_catalog_item_id, parent_version_id, child_catalog_item_id,
                         dependency_kind, part_name, ordinal, child_version_label)
                    VALUES (@parentItem, @parentVersion, @childItem, @kind, @part, @ord, @childLabel)
                    """;
                ins.Parameters.Add(new SqliteParameter("@parentItem", parentCatalogItemId));
                ins.Parameters.Add(new SqliteParameter("@parentVersion", parentVersionId));
                ins.Parameters.Add(new SqliteParameter("@childItem", dep.ChildCatalogItemId));
                ins.Parameters.Add(new SqliteParameter("@kind", dep.Kind));
                ins.Parameters.Add(new SqliteParameter("@part", (object?)dep.PartName ?? DBNull.Value));
                ins.Parameters.Add(new SqliteParameter("@ord", ordinal));
                ins.Parameters.Add(new SqliteParameter("@childLabel", (object?)dep.ChildVersionLabel ?? DBNull.Value));
                await ins.ExecuteNonQueryAsync(ct);
                ordinal++;
            }

            tx.Commit();
            SmartConLogger.Info($"Persisted {ordinal} dependency links (input={dependencies.Count})");
            return ordinal;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<int> ReplaceForCurrentVersionAsync(
        string parentCatalogItemId,
        IReadOnlyList<FamilyDependencyInfo> dependencies,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(parentCatalogItemId))
            throw new ArgumentException("parentCatalogItemId is required", nameof(parentCatalogItemId));

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        string? versionId = null;
        using (var find = connection.CreateCommand())
        {
            find.CommandText = """
                SELECT cv.id
                FROM catalog_versions cv
                INNER JOIN catalog_items ci
                    ON ci.id = cv.catalog_item_id
                   AND ci.current_version_label = cv.version_label
                WHERE ci.id = @itemId
                """;
            find.Parameters.Add(new SqliteParameter("@itemId", parentCatalogItemId));
            versionId = (string?)await find.ExecuteScalarAsync(ct);
        }

        if (versionId is null)
        {
            SmartConLogger.Warn(
                $"ReplaceForCurrentVersion: parent '{parentCatalogItemId}' has no current version — links not written. " +
                "[Action: проверьте, что родительский элемент каталога действительно импортирован; связи можно создать повторным импортом родителя]");
            return 0;
        }

        var written = await ReplaceForVersionCoreAsync(
            connection, parentCatalogItemId, versionId, dependencies, ct);
        return written;
    }

    public async Task<IReadOnlyList<FamilyDependencyInfo>> GetForCurrentVersionAsync(
        string parentCatalogItemId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(parentCatalogItemId))
            throw new ArgumentException("parentCatalogItemId is required", nameof(parentCatalogItemId));

        using var _scope = SmartConLogger.BeginScope("FamilyDepRepo",
            ("Method", "GetForCurrentVersionAsync"),
            ("CatalogItemId", parentCatalogItemId));

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT fd.child_catalog_item_id, fd.dependency_kind, fd.part_name, fd.ordinal, fd.child_version_label
            FROM family_dependencies fd
            JOIN catalog_versions cv ON cv.id = fd.parent_version_id
            INNER JOIN catalog_items ci
                ON ci.id = cv.catalog_item_id
               AND ci.current_version_label = cv.version_label
            WHERE fd.parent_catalog_item_id = @itemId
            ORDER BY fd.ordinal
            """;
        cmd.Parameters.Add(new SqliteParameter("@itemId", parentCatalogItemId));

        var result = new List<FamilyDependencyInfo>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new FamilyDependencyInfo(
                ChildCatalogItemId: reader.GetString(0),
                Kind: reader.GetString(1),
                PartName: reader.IsDBNull(2) ? null : reader.GetString(2),
                Ordinal: reader.GetInt32(3),
                ChildVersionLabel: reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        SmartConLogger.Info($"Loaded {result.Count} dependency links from catalog DB");
        return result;
    }

    public async Task<IReadOnlyList<FamilyDependencyReference>> GetReferencingParentsAsync(
        string childCatalogItemId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(childCatalogItemId))
            throw new ArgumentException("childCatalogItemId is required", nameof(childCatalogItemId));

        var batch = await GetReferencingParentsBatchAsync(new[] { childCatalogItemId }, ct);
        return batch.TryGetValue(childCatalogItemId, out var references)
            ? references
            : Array.Empty<FamilyDependencyReference>();
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<FamilyDependencyReference>>> GetReferencingParentsBatchAsync(
        IReadOnlyCollection<string> childCatalogItemIds,
        CancellationToken ct = default)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(childCatalogItemIds);
#else
        if (childCatalogItemIds is null) throw new ArgumentNullException(nameof(childCatalogItemIds));
#endif

        var result = new Dictionary<string, IReadOnlyList<FamilyDependencyReference>>(StringComparer.Ordinal);
        var ids = childCatalogItemIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) return result;

        using var _scope = SmartConLogger.BeginScope("FamilyDepRepo",
            ("Method", "GetReferencingParentsBatch"),
            ("Count", ids.Count));

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        var parameterNames = new List<string>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            var parameterName = "@child" + i;
            parameterNames.Add(parameterName);
            cmd.Parameters.Add(new SqliteParameter(parameterName, ids[i]));
        }
        // DISTINCT collapses multiple links of the same parent version
        // (different kinds / part names) into one guard reference.
        cmd.CommandText = $"""
            SELECT DISTINCT fd.child_catalog_item_id, ci.id, ci.name, cv.version_label,
                   CASE WHEN ci.current_version_label = cv.version_label THEN 1 ELSE 0 END
            FROM family_dependencies fd
            JOIN catalog_items ci ON ci.id = fd.parent_catalog_item_id
            JOIN catalog_versions cv ON cv.id = fd.parent_version_id
            WHERE fd.child_catalog_item_id IN ({string.Join(", ", parameterNames)})
            ORDER BY ci.name, cv.version_label
            """;

        var grouped = new Dictionary<string, List<FamilyDependencyReference>>(StringComparer.Ordinal);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var childId = reader.GetString(0);
            if (!grouped.TryGetValue(childId, out var list))
            {
                list = new List<FamilyDependencyReference>();
                grouped[childId] = list;
            }
            list.Add(new FamilyDependencyReference(
                ParentCatalogItemId: reader.GetString(1),
                ParentName: reader.GetString(2),
                VersionLabel: reader.GetString(3),
                IsCurrentVersion: reader.GetInt32(4) == 1));
        }

        foreach (var pair in grouped)
        {
            result[pair.Key] = pair.Value;
        }

        SmartConLogger.Debug($"Reverse dependency lookup: {grouped.Count} of {ids.Count} items are referenced");
        return result;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<FamilyDependencyDrift>>> GetDependencyDriftBatchAsync(
        IReadOnlyCollection<string> parentCatalogItemIds,
        CancellationToken ct = default)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(parentCatalogItemIds);
#else
        if (parentCatalogItemIds is null) throw new ArgumentNullException(nameof(parentCatalogItemIds));
#endif

        var result = new Dictionary<string, IReadOnlyList<FamilyDependencyDrift>>(StringComparer.Ordinal);
        var ids = parentCatalogItemIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) return result;

        using var _scope = SmartConLogger.BeginScope("FamilyDepRepo",
            ("Method", "GetDependencyDriftBatch"),
            ("Count", ids.Count));

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        var parameterNames = new List<string>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            var parameterName = "@parent" + i;
            parameterNames.Add(parameterName);
            cmd.Parameters.Add(new SqliteParameter(parameterName, ids[i]));
        }
        // Drift = embedded child version differs from the child's current
        // active version. NULL child_version_label (legacy V29 links, or a
        // skipped child whose content matched no stored version) is unknown
        // — never drifted. Only the parent's CURRENT version links count:
        // archived versions keep their historical embedded labels.
        // Loadable parents only: a SYSTEM parent's sync resolves children
        // dynamically at their ACTIVE version (no physically embedded copy),
        // so routing-link drift would be a false positive there.
        cmd.CommandText = $"""
            SELECT fd.parent_catalog_item_id, fd.child_catalog_item_id, child.name,
                   fd.child_version_label, child.current_version_label
            FROM family_dependencies fd
            JOIN catalog_versions pv ON pv.id = fd.parent_version_id
            JOIN catalog_items parent
                ON parent.id = pv.catalog_item_id
               AND parent.current_version_label = pv.version_label
            JOIN catalog_items child ON child.id = fd.child_catalog_item_id
            WHERE fd.parent_catalog_item_id IN ({string.Join(", ", parameterNames)})
              AND parent.family_source = 'loadable'
              AND fd.child_version_label IS NOT NULL
              AND fd.child_version_label <> child.current_version_label
            ORDER BY fd.parent_catalog_item_id, fd.ordinal
            """;

        var grouped = new Dictionary<string, List<FamilyDependencyDrift>>(StringComparer.Ordinal);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var parentId = reader.GetString(0);
            var embeddedLabel = reader.GetString(3);
            var currentLabel = reader.GetString(4);

            // E2 (#209, owner decision): drift is DIRECTIONAL — only an
            // embedded version STRICTLY OLDER than the active one is a
            // problem (loading the parent would plant an outdated copy).
            // "Embedded newer than active" (a fresh import carrying the
            // newest nested content) is not a defect: the badge/block would
            // fire immediately after every such import (validator M1).
            if (!IsStrictlyOlder(embeddedLabel, currentLabel))
            {
                continue;
            }

            if (!grouped.TryGetValue(parentId, out var list))
            {
                list = new List<FamilyDependencyDrift>();
                grouped[parentId] = list;
            }
            list.Add(new FamilyDependencyDrift(
                ParentCatalogItemId: parentId,
                ChildCatalogItemId: reader.GetString(1),
                ChildName: reader.GetString(2),
                EmbeddedVersionLabel: embeddedLabel,
                CurrentVersionLabel: currentLabel));
        }

        foreach (var pair in grouped)
        {
            result[pair.Key] = pair.Value;
        }

        SmartConLogger.Debug($"Dependency drift lookup: {grouped.Count} of {ids.Count} parents have drifted dependencies");
        return result;
    }

    /// <summary>
    /// Directional drift check (E2, #209): <c>true</c> only when the
    /// embedded label is strictly older than the active one. Labels are the
    /// catalog's monotonic "vN" — parsed numerically; an unparseable
    /// mismatch is conservatively treated as drift (badge + block).
    /// </summary>
    private static bool IsStrictlyOlder(string embeddedLabel, string currentLabel)
    {
        var embedded = ParseVersionNumber(embeddedLabel);
        var current = ParseVersionNumber(currentLabel);
        if (embedded is null || current is null)
        {
            return !string.Equals(embeddedLabel, currentLabel, StringComparison.Ordinal);
        }
        return embedded.Value < current.Value;
    }

    private static int? ParseVersionNumber(string? label)
    {
        if (label is null || label.Length < 2) return null;
        if (label[0] != 'v' && label[0] != 'V') return null;
#if NET8_0_OR_GREATER
        return int.TryParse(
            label.AsSpan(1),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var n)
            ? n
            : null;
#else
        return int.TryParse(
            label.Substring(1),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var n)
            ? n
            : null;
#endif
    }
}
