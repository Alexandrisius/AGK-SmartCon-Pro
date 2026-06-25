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

    public async Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemAsync(string catalogItemId, CancellationToken ct = default)
    {
        var result = new List<FamilyTypeDescriptor>();

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, type_name, sort_order, version_id, file_id, extraction_run_id, type_unique_id FROM family_types WHERE catalog_item_id = @itemId ORDER BY sort_order";
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
                reader.IsDBNull(6) ? null : reader.GetString(6)));
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
            cmd.CommandText = "SELECT id, type_name, sort_order, version_id, file_id, extraction_run_id, type_unique_id FROM family_types WHERE catalog_item_id = @itemId AND version_id IS NULL ORDER BY sort_order";
            cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        }
        else
        {
            cmd.CommandText = "SELECT id, type_name, sort_order, version_id, file_id, extraction_run_id, type_unique_id FROM family_types WHERE catalog_item_id = @itemId AND version_id = @versionId ORDER BY sort_order";
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
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return result.AsReadOnly();
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<FamilyTypeDescriptor>>> GetAllTypesBatchAsync(IEnumerable<string> catalogItemIds, CancellationToken ct = default)
    {
        var idList = catalogItemIds.ToList();
        if (idList.Count == 0) return new Dictionary<string, IReadOnlyList<FamilyTypeDescriptor>>();

        var result = new Dictionary<string, IReadOnlyList<FamilyTypeDescriptor>>();

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        var placeholders = string.Join(",", Enumerable.Range(0, idList.Count).Select(i => $"@p{i}"));
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT id, catalog_item_id, type_name, sort_order, version_id, file_id, extraction_run_id, type_unique_id FROM family_types WHERE catalog_item_id IN ({placeholders}) ORDER BY sort_order";
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
                reader.IsDBNull(7) ? null : reader.GetString(7));

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
    /// v2.0.0 (ADR-036): single-transaction DELETE+INSERT. Replaces the old
    /// <c>SaveTypesAsync</c> (DELETE+INSERT) and <c>SaveTypesForRunAsync</c>
    /// (UPSERT-without-DELETE) pair. The old UPSERT variant left ghost types
    /// whenever a type was removed from the .rfa between imports.
    ///
    /// Scope is determined by the (versionId, fileId) tuple:
    /// <list type="bullet">
    /// <item><c>(null, null)</c> — orchestrator case
    /// (<c>LoadableFamilyImportOrchestrator</c>,
    /// <c>SystemFamilyImportOrchestrator</c> for project case). DELETE
    /// removes all types for the catalog item (replace-all).</item>
    /// <item><c>(versionId, *)</c> — active family import case. DELETE
    /// removes all types whose <c>family_types.version_id</c> resolves to
    /// a <c>catalog_versions</c> row with the same <c>version_label</c>
    /// as the supplied <paramref name="versionId"/>. This is the fix for
    /// Bug #1: <c>FamilyImportResult.VersionId</c> is a fresh
    /// <see cref="Guid.NewGuid"/> on every import (see
    /// <c>LocalFamilyImportService.ImportFileAsync:138</c>), even when the
    /// import overwrites the same version. Filtering by <c>versionId</c>
    /// directly would miss all previous rows.</item>
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
            // 1. DELETE existing types.
            //
            //    Three cases:
            //
            //    (a) versionId == null AND fileId == null — orchestrator case
            //        (LoadableFamilyImportOrchestrator, SystemFamilyImportOrchestrator
            //        for system/loadable families from the project). The
            //        caller has no stable version handle; the whole
            //        catalog item is treated as "replace everything".
            //
            //    (b) versionId != null — active family import case. The
            //        catalogItemId/versionId is from a fresh
            //        Guid.NewGuid() per import, but the version_label
            //        ("v3") is stable. We resolve version_label through
            //        catalog_versions and DELETE all family_types rows
            //        with the same version_label. This fixes Bug #1
            //        (ghost types).
            //
            //    (c) fileId != null but versionId == null — defensive
            //        no-op. Should not occur in practice.
            //
            //    FK on extracted_attribute_values.type_id has ON DELETE
            //    CASCADE (migration V15), so attribute values for removed
            //    types are cleaned up automatically at the DB level.
            using (var delCmd = connection.CreateCommand())
            {
                if (versionId is null && fileId is null)
                {
                    delCmd.CommandText = "DELETE FROM family_types WHERE catalog_item_id = @itemId";
                    delCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                    int deleted = await delCmd.ExecuteNonQueryAsync(ct);
                    SmartConLogger.Debug(
                        $"SyncTypesAsync[orchestrator]: catalogItemId={catalogItemId}, deleted={deleted} pre-existing type row(s) (replace-all)");
                }
                else if (versionId is not null)
                {
                    // Active family import: collapse to current version
                    // (rev #3 fix for issue #85). The user edits a
                    // managed .rfa in Revit and clicks "Импорт активного
                    // файла" — every such click produces a fresh
                    // catalog_versions row in
                    // LocalFamilyImportService.ImportFileAsync:138 (new
                    // Guid.NewGuid()). Even when the version_label is
                    // "v3", the DB can end up with multiple
                    // catalog_versions rows for the same
                    // (catalog_item_id, version_label) triple — only
                    // kept apart by the revit_major_version column of
                    // the UNIQUE constraint. The earlier JOIN-by-
                    // versionLabel DELETE was correct in intent, but a
                    // single active import run only sees one of the
                    // rows, so the other rows (from prior imports) keep
                    // their family_types rows alive and the UI keeps
                    // showing ghost types.
                    //
                    // The fix: for active family import, DELETE all
                    // family_types rows for this catalog_item
                    // regardless of version_id. This is destructive on
                    // multi-version families, but the user just
                    // clicked "Импорт активного файла" — they want
                    // the catalog to reflect the current .rfa, not a
                    // historical union of every version that ever
                    // lived on disk. The catalog_versions history is
                    // preserved (so a future "rollback" can still see
                    // the labels) but the type set collapses to the
                    // single currently-active version.
                    delCmd.CommandText = "DELETE FROM family_types WHERE catalog_item_id = @itemId";
                    delCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                    int deleted = await delCmd.ExecuteNonQueryAsync(ct);
                    SmartConLogger.Debug(
                        $"SyncTypesAsync[active-import]: catalogItemId={catalogItemId}, versionId={versionId}, fileId={fileId ?? "<null>"}, deleted={deleted} pre-existing type row(s) across ALL versions (collapse to current)");
                }
                else
                {
                    SmartConLogger.Warn(
                        $"SyncTypesAsync: catalogItemId={catalogItemId}, fileId={fileId} but versionId is null — skipping DELETE (defensive no-op) [Action: проверьте вызывающий код — versionId не должен быть null когда fileId указан; текущая семантика может оставлять orphan types в БД для этого catalog_item]");
                }
            }

            // 2. INSERT (or UPSERT) the supplied types in sort-order.
            //    The ON CONFLICT clause is a safety net for callers that pass
            //    a duplicate name within the same list (shouldn't happen but
            //    doesn't hurt to be defensive).
            for (var i = 0; i < types.Count; i++)
            {
                using var upsertCmd = connection.CreateCommand();
                upsertCmd.CommandText = """
                    INSERT INTO family_types (id, catalog_item_id, type_name, sort_order, version_id, file_id, extraction_run_id, type_unique_id)
                    VALUES (@id, @itemId, @name, @sort, @versionId, @fileId, @runId, @uniqueId)
                    ON CONFLICT(catalog_item_id, type_name) DO UPDATE SET
                        sort_order = excluded.sort_order,
                        version_id = excluded.version_id,
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
                var returnedId = await upsertCmd.ExecuteScalarAsync(ct);
                if (returnedId is null || returnedId is DBNull)
                {
                    throw new InvalidOperationException(
                        $"SyncTypesAsync: RETURNING id returned null for type '{types[i].Name}' " +
                        $"(catalog_item_id='{catalogItemId}'). Possible SQLite version < 3.35.");
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
}
