using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class LocalFamilyTypeRepository : IFamilyTypeRepository
{
    private readonly LocalCatalogDatabase _database;

    public LocalFamilyTypeRepository(LocalCatalogDatabase database)
    {
        _database = database;
    }

    /// <summary>
    /// Returns the types of the <b>active version</b> of a catalog item —
    /// i.e. the version pointed at by
    /// <c>catalog_items.current_version_label</c> (resolved to a
    /// <c>catalog_versions.id</c>). For the orchestrator case (project
    /// families without a <c>catalog_versions</c> row), returns the
    /// version-less type rows (<c>version_id IS NULL</c>).
    ///
    /// v2.1.0 (ADR-041 rev #2): pre-V18 this method returned the UNION of
    /// all type rows for a catalog item — which the UI then rendered as
    /// children of the family leaf node, regardless of version. With
    /// per-version type storage (V18 migration), the union would contain
    /// duplicates ("100" from v1, "100" from v2). Filtering to the active
    /// version makes the UI follow <c>SetActiveVersionAsync</c> correctly.
    /// </summary>
    public async Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemAsync(string catalogItemId, CancellationToken ct = default)
    {
        var result = new List<FamilyTypeDescriptor>();

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, type_name, sort_order, version_id, file_id, extraction_run_id, type_unique_id, family_name
            FROM family_types
            WHERE catalog_item_id = @itemId
              AND (
                version_id = (
                  SELECT cv.id FROM catalog_versions cv
                  INNER JOIN catalog_items ci ON ci.id = cv.catalog_item_id
                    AND ci.current_version_label = cv.version_label
                  WHERE cv.catalog_item_id = @itemId
                  LIMIT 1
                )
                OR (version_id IS NULL AND NOT EXISTS (
                  SELECT 1 FROM catalog_versions WHERE catalog_item_id = @itemId
                ))
              )
            ORDER BY sort_order
            """;
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new FamilyTypeDescriptor(
                reader.GetString(0),
                catalogItemId,
                reader.GetString(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                NormalizeFamilyName(reader.IsDBNull(7) ? null : reader.GetString(7))));
        }

        return result.AsReadOnly();
    }

    public async Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemVersionAsync(string catalogItemId, string? versionId, CancellationToken ct = default)
    {
        var result = new List<FamilyTypeDescriptor>();

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();

        if (versionId is null)
        {
            cmd.CommandText = "SELECT id, type_name, sort_order, version_id, file_id, extraction_run_id, type_unique_id, family_name FROM family_types WHERE catalog_item_id = @itemId AND version_id IS NULL ORDER BY sort_order";
            cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        }
        else
        {
            cmd.CommandText = "SELECT id, type_name, sort_order, version_id, file_id, extraction_run_id, type_unique_id, family_name FROM family_types WHERE catalog_item_id = @itemId AND version_id = @versionId ORDER BY sort_order";
            cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
            cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
        }

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new FamilyTypeDescriptor(
                reader.GetString(0),
                catalogItemId,
                reader.GetString(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                NormalizeFamilyName(reader.IsDBNull(7) ? null : reader.GetString(7))));
        }

        return result.AsReadOnly();
    }

    /// <summary>
    /// Batch variant of <see cref="GetTypesForItemAsync"/>. Returns a
    /// {catalogItemId → types of active version} map for the supplied
    /// catalog item ids. Same active-version filter as
    /// <see cref="GetTypesForItemAsync"/> (v2.1.0 / ADR-041 rev #2):
    /// types of the active version (resolved via
    /// <c>catalog_items.current_version_label</c> →
    /// <c>catalog_versions.id</c>), with the orchestrator fallback for
    /// project families (<c>version_id IS NULL</c> when no
    /// <c>catalog_versions</c> rows exist for the item).
    ///
    /// The correlated subquery references <c>family_types.catalog_item_id</c>
    /// via the outer WHERE, so SQLite evaluates it per row.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<FamilyTypeDescriptor>>> GetAllTypesBatchAsync(IEnumerable<string> catalogItemIds, CancellationToken ct = default)
    {
        var idList = catalogItemIds.ToList();
        if (idList.Count == 0) return new Dictionary<string, IReadOnlyList<FamilyTypeDescriptor>>();

        var result = new Dictionary<string, IReadOnlyList<FamilyTypeDescriptor>>();

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        var placeholders = string.Join(",", Enumerable.Range(0, idList.Count).Select(i => $"@p{i}"));
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, catalog_item_id, type_name, sort_order, version_id, file_id, extraction_run_id, type_unique_id, family_name
            FROM family_types
            WHERE catalog_item_id IN ({placeholders})
              AND (
                version_id = (
                  SELECT cv.id FROM catalog_versions cv
                  INNER JOIN catalog_items ci ON ci.id = cv.catalog_item_id
                    AND ci.current_version_label = cv.version_label
                  WHERE cv.catalog_item_id = family_types.catalog_item_id
                  LIMIT 1
                )
                OR (version_id IS NULL AND NOT EXISTS (
                  SELECT 1 FROM catalog_versions WHERE catalog_item_id = family_types.catalog_item_id
                ))
              )
            ORDER BY sort_order
            """;
        for (var i = 0; i < idList.Count; i++)
            cmd.Parameters.Add(new SqliteParameter($"@p{i}", idList[i]));

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var itemId = reader.GetString(1);
            var type = new FamilyTypeDescriptor(
                reader.GetString(0),
                itemId,
                reader.GetString(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                NormalizeFamilyName(reader.IsDBNull(8) ? null : reader.GetString(8)));

            if (!result.TryGetValue(itemId, out var list))
            {
                list = new List<FamilyTypeDescriptor>();
                result[itemId] = list;
            }
            ((List<FamilyTypeDescriptor>)list).Add(type);
        }

        return result;
    }

    /// <summary>
    /// v2.0.0 (ADR-036, ADR-041 rev #2): single-transaction DELETE+INSERT
    /// with <b>version-scoped</b> scope. Types of different versions
    /// coexist in <c>family_types</c> (per-version UNIQUE — migration V18;
    /// extended with <c>family_name</c> in V26 / Issue #183 — a system type
    /// is identified by (family, name), never by name alone),
    /// so rollback via <c>SetActiveVersionAsync</c> still finds the previous
    /// version's type rows intact.
    ///
    /// Scope is determined by the (versionId, fileId) tuple:
    /// <list type="bullet">
    /// <item><c>(null, null)</c> — orchestrator case
    /// (<c>LoadableFamilyImportOrchestrator</c>,
    /// <c>SystemFamilyImportOrchestrator</c> for project case, which has no
    /// stable version handle). DELETE removes only rows with
    /// <c>version_id IS NULL</c>. INSERT uses
    /// <c>ON CONFLICT(catalog_item_id, version_id, family_name, type_name)</c>
    /// — which the table-level UNIQUE covers for versioned rows; for NULL
    /// version_id the preceding DELETE makes conflicts impossible in
    /// practice (the partial orchestrator index is a fail-fast net).</item>
    /// <item><c>(versionId, *)</c> — active family import case. DELETE
    /// removes only rows where <c>family_types.version_id = @versionId</c>.
    /// Types of other versions are preserved. <c>FamilyImportResult.VersionId</c>
    /// for a new import is a fresh <see cref="Guid.NewGuid"/>, so this DELETE
    /// is a no-op on the first import of a version — but becomes a replace
    /// when <c>OverwriteCurrentAsync</c> reuses the same
    /// <c>catalog_versions.id</c> (ADR-040).</item>
    /// <item><c>(null, fileId)</c> — defensive no-op. Should not occur in
    /// practice.</item>
    /// </list>
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> SyncTypesAsync(
        string catalogItemId,
        string? versionId,
        string? fileId,
        string runId,
        IReadOnlyList<FamilyTypeDescriptor> types,
        CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var tx = connection.BeginTransaction();

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // 1. DELETE existing types (version-scoped).
            //
            //    (a) versionId == null AND fileId == null — orchestrator case.
            //        DELETE only rows with version_id IS NULL. Project families
            //        (system/loadable imported from the active project) never
            //        carry a version_id (they have no catalog_versions row),
            //        so this keeps them disjoint from per-version types.
            //
            //    (b) versionId != null — active family import case. DELETE
            //        only rows for THIS version_id. Other versions stay
            //        untouched so the user can roll back via
            //        SetActiveVersionAsync and still find their types.
            //
            //    (c) fileId != null but versionId == null — defensive no-op.
            //
            //    FK on extracted_attribute_values.type_id has ON DELETE
            //    CASCADE (migration V15), so attribute values for removed
            //    types are cleaned up automatically at the DB level.
            using (var delCmd = connection.CreateCommand())
            {
                if (versionId is null && fileId is null)
                {
                    delCmd.CommandText = "DELETE FROM family_types WHERE catalog_item_id = @itemId AND version_id IS NULL";
                    delCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                    int deleted = await delCmd.ExecuteNonQueryAsync(ct);
                    SmartConLogger.Debug(
                        $"SyncTypesAsync[orchestrator]: catalogItemId={catalogItemId}, deleted={deleted} pre-existing type row(s) with version_id IS NULL (replace-all orchestrator scope)");
                }
                else if (versionId is not null)
                {
                    // Version-scoped DELETE: only types of THIS version are
                    // removed; types of other versions stay so rollback
                    // (SetActiveVersionAsync) can still find them. ADR-041 rev #2.
                    delCmd.CommandText = "DELETE FROM family_types WHERE catalog_item_id = @itemId AND version_id = @versionId";
                    delCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                    delCmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
                    int deleted = await delCmd.ExecuteNonQueryAsync(ct);
                    SmartConLogger.Debug(
                        $"SyncTypesAsync[active-import]: catalogItemId={catalogItemId}, versionId={versionId}, fileId={fileId ?? "<null>"}, deleted={deleted} pre-existing type row(s) for this version (version-scoped)");
                }
                else
                {
                    SmartConLogger.Warn(
                        $"SyncTypesAsync: catalogItemId={catalogItemId}, fileId={fileId} but versionId is null — skipping DELETE (defensive no-op) [Action: проверьте вызывающий код — versionId не должен быть null когда fileId указан; текущая семантика может оставлять orphan types в БД для этого catalog_item]");
                }
            }

            // 2. INSERT (or UPSERT) the supplied types in sort-order. V26
            //    (#183): conflict target is (catalog_item_id, version_id,
            //    family_name, type_name) — a system type is identified by
            //    (family, name), so "Стандарт" of two conduit families are
            //    two rows, not an UPSERT collision. family_name is stored
            //    as '' (never NULL) to keep the UNIQUE constraint strict.
            for (var i = 0; i < types.Count; i++)
            {
                using var upsertCmd = connection.CreateCommand();
                upsertCmd.CommandText = """
                    INSERT INTO family_types (id, catalog_item_id, type_name, sort_order, version_id, file_id, extraction_run_id, type_unique_id, family_name)
                    VALUES (@id, @itemId, @name, @sort, @versionId, @fileId, @runId, @uniqueId, @familyName)
                    ON CONFLICT(catalog_item_id, version_id, family_name, type_name) DO UPDATE SET
                        sort_order = excluded.sort_order,
                        file_id = excluded.file_id,
                        extraction_run_id = excluded.extraction_run_id,
                        type_unique_id = COALESCE(excluded.type_unique_id, family_types.type_unique_id)
                    RETURNING id
                    """;
                upsertCmd.Parameters.Add(new SqliteParameter("@id", types[i].Id));
                upsertCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                upsertCmd.Parameters.Add(new SqliteParameter("@name", types[i].Name));
                upsertCmd.Parameters.Add(new SqliteParameter("@sort", types[i].SortOrder));
                upsertCmd.Parameters.Add(new SqliteParameter("@versionId", (object?)versionId ?? DBNull.Value));
                upsertCmd.Parameters.Add(new SqliteParameter("@fileId", (object?)fileId ?? DBNull.Value));
                upsertCmd.Parameters.Add(new SqliteParameter("@runId", runId));
                upsertCmd.Parameters.Add(new SqliteParameter("@uniqueId", (object?)types[i].UniqueId ?? DBNull.Value));
                upsertCmd.Parameters.Add(new SqliteParameter("@familyName", types[i].FamilyName ?? string.Empty));
                var returnedId = await upsertCmd.ExecuteScalarAsync(ct);
                if (returnedId is null || returnedId is DBNull)
                {
                    throw new InvalidOperationException(
                        $"SyncTypesAsync: RETURNING id returned null for type '{types[i].Name}' " +
                        $"(catalog_item_id='{catalogItemId}', version_id='{versionId ?? "<null>"}'). Possible SQLite version < 3.35.");
                }
                result[types[i].Name] = (string)returnedId;
            }

            tx.Commit();
            SmartConLogger.Debug(
                $"SyncTypesAsync: catalogItemId={catalogItemId}, versionId={versionId ?? "<null>"}, inserted={types.Count} type row(s)");
            return result;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<bool> HasTypesAsync(string catalogItemId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM family_types WHERE catalog_item_id = @itemId";
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l && l > 0;
    }

    /// <summary>
    /// V26 (#183): the DB stores family_name as '' (never NULL, keeps the
    /// UNIQUE strict); the domain model exposes it as null ("no family
    /// known/applicable").
    /// </summary>
    private static string? NormalizeFamilyName(string? dbValue)
        => string.IsNullOrEmpty(dbValue) ? null : dbValue;
}
