using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Actualization;

/// <summary>
/// CRITICAL actualization task (Id=<c>hash-v2</c>): recalculates stale
/// (format v1 / NULL) content hashes to the rename-invariant format v2
/// (Issue #126, ADR-049). Owns the <c>hash_format_version</c> marker
/// semantics: NULL/1 pending, 2 current, -1/-2 terminal (unreadable /
/// missing — never retried). System-family rows are re-flagged in the
/// file-free pass (their v1 hashes are already rename-invariant).
/// </summary>
internal sealed class HashFormatActualizationTask : IDatabaseActualizationTask
{
    private readonly LocalCatalogDatabase _database;
    private readonly IFamilyContentHasher _contentHasher;

    public HashFormatActualizationTask(
        LocalCatalogDatabase database,
        IFamilyContentHasher contentHasher)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _contentHasher = contentHasher ?? throw new ArgumentNullException(nameof(contentHasher));
    }

    public string Id => "hash-v2";
    public int Order => 10;
    public bool IsCritical => true;

    public async Task<int> CountPendingAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // Loadable groups processable in the running Revit.
        int loadable;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(*) FROM (
                    SELECT cv.catalog_item_id, cv.version_label
                    FROM catalog_versions cv
                    JOIN catalog_items ci ON ci.id = cv.catalog_item_id
                    JOIN family_files ff ON ff.id = cv.file_id
                    WHERE ci.family_source = 'loadable'
                      AND (cv.hash_format_version IS NULL OR cv.hash_format_version NOT IN (2, -1, -2))
                      AND cv.revit_major_version <= @maxRevit
                    GROUP BY cv.catalog_item_id, cv.version_label
                )
                """;
            cmd.Parameters.Add(new SqliteParameter("@maxRevit", revitMajorVersion));
            var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            loadable = obj is long l ? (int)l : 0;
        }

        int system;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(*)
                FROM catalog_versions cv
                JOIN catalog_items ci ON ci.id = cv.catalog_item_id
                WHERE ci.family_source = 'system'
                  AND (cv.hash_format_version IS NULL OR cv.hash_format_version NOT IN (2, -1, -2))
                """;
            var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            system = obj is long s ? (int)s : 0;
        }

        return loadable + system;
    }

    public async Task<IReadOnlyCollection<string>> LoadPendingGroupKeysAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        // ALL stale loadable groups, including newer-Revit-only ones — the
        // engine classifies openability and reports them as newer-pending.
        var keys = new HashSet<string>(StringComparer.Ordinal);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT cv.catalog_item_id, cv.version_label
            FROM catalog_versions cv
            JOIN catalog_items ci ON ci.id = cv.catalog_item_id
            WHERE ci.family_source = 'loadable'
              AND (cv.hash_format_version IS NULL OR cv.hash_format_version NOT IN (2, -1, -2))
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            keys.Add(reader.GetString(0) + "|" + reader.GetString(1));
        }
        return keys;
    }

    public async Task<NewerOnlyPendingInfo> GetNewerOnlyPendingAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        // Groups whose EVERY variant requires a newer Revit: pending, but
        // no variant of the group is openable here. Openability is checked
        // across ALL variants of the group — not only the pending ones —
        // because the engine applies the result to every variant.
        // RequiredRevitVersion = the minimum Revit making ALL newer-only
        // groups processable in one pass (MAX over groups of the group's
        // minimum variant version).
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*), COALESCE(MAX((
                SELECT MIN(cv2.revit_major_version) FROM catalog_versions cv2
                WHERE cv2.catalog_item_id = p.itemId AND cv2.version_label = p.label)), 0)
            FROM (
                SELECT DISTINCT cv.catalog_item_id AS itemId, cv.version_label AS label
                FROM catalog_versions cv
                JOIN catalog_items ci ON ci.id = cv.catalog_item_id
                WHERE ci.family_source = 'loadable'
                  AND (cv.hash_format_version IS NULL OR cv.hash_format_version NOT IN (2, -1, -2))
            ) p
            WHERE NOT EXISTS (
                SELECT 1 FROM catalog_versions cv3
                WHERE cv3.catalog_item_id = p.itemId AND cv3.version_label = p.label
                  AND cv3.revit_major_version <= @maxRevit
            )
            """;
        cmd.Parameters.Add(new SqliteParameter("@maxRevit", revitMajorVersion));
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return NewerOnlyPendingInfo.None;
        var count = reader.GetInt32(0);
        var required = reader.GetInt32(1);
        return new NewerOnlyPendingInfo(count, required);
    }

    /// <summary>
    /// System-family rows are re-flagged to format v2 instantly (their v1
    /// canonical string never contained a name — the hashes are already
    /// rename-invariant; recomputing them from the isolated .rvt would
    /// risk a mismatch against project-extracted hashes).
    /// </summary>
    public async Task<int> RunFileFreePassAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var tx = connection.BeginTransaction();
        try
        {
            int versions;
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE catalog_versions SET hash_format_version = 2
                    WHERE (hash_format_version IS NULL OR hash_format_version NOT IN (2, -1, -2))
                      AND catalog_item_id IN (SELECT id FROM catalog_items WHERE family_source = 'system')
                    """;
                versions = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE catalog_items SET hash_format_version = 2
                    WHERE family_source = 'system'
                      AND (hash_format_version IS NULL OR hash_format_version NOT IN (2, -1, -2))
                    """;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            tx.Commit();
            if (versions > 0)
                SmartConLogger.Info($"System rows re-flagged to hash format v2: {versions}");
            return versions;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task ApplyAsync(FamilyActualizationContext context, CancellationToken ct = default)
    {
        var hash = _contentHasher.ComputeForLoadable(context.Snapshot)?.HexString;
        if (hash is null)
        {
            SmartConLogger.Warn(
                $"Hash computation returned null for '{context.OpenedVariant.FileName}' (item '{context.Group.ItemName}') " +
                $"[Action: версия помечена как пропущенная (-1); переимпортируйте семейство для восстановления дедупликации]");
            await WriteMarkerAsync(context.Group.Variants, FamilyContentHashFormat.RecalculationSkipped, ct)
                .ConfigureAwait(false);
            return;
        }

        // One UPDATE for ALL Revit variants of the group (the content is
        // identical across variants), plus the item's denormalized hash
        // when the group is the ACTIVE label (same rule as
        // SetActiveVersionAsync).
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var tx = connection.BeginTransaction();
        try
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"""
                    UPDATE catalog_versions
                    SET content_hash = @hash, hash_format_version = 2
                    WHERE id IN ({VariantIdParams(cmd, context.Group.Variants)})
                    """;
                cmd.Parameters.Add(new SqliteParameter("@hash", hash));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (context.Group.IsActiveLabel)
            {
                using var itemCmd = connection.CreateCommand();
                itemCmd.Transaction = tx;
                itemCmd.CommandText = """
                    UPDATE catalog_items
                    SET content_hash = @hash, hash_format_version = 2, updated_at_utc = @now
                    WHERE id = @itemId
                    """;
                itemCmd.Parameters.Add(new SqliteParameter("@hash", hash));
                itemCmd.Parameters.Add(new SqliteParameter("@now", DateTimeOffset.UtcNow.ToString("o")));
                itemCmd.Parameters.Add(new SqliteParameter("@itemId", context.Group.CatalogItemId));
                await itemCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task HandleGroupFailureAsync(
        ActualizationGroup group, ActualizationFailureKind kind, CancellationToken ct = default)
    {
        // Terminal markers (ADR-050 §2): the hash criterion never retries —
        // -2 missing file, -1 unreadable file.
        var marker = kind == ActualizationFailureKind.MissingFile
            ? FamilyContentHashFormat.RecalculationMissing
            : FamilyContentHashFormat.RecalculationSkipped;
        await WriteMarkerAsync(group.Variants, marker, ct).ConfigureAwait(false);
    }

    private async Task WriteMarkerAsync(
        IReadOnlyList<ActualizationVariant> variants, int marker, CancellationToken ct)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            UPDATE catalog_versions
            SET hash_format_version = @marker
            WHERE id IN ({VariantIdParams(cmd, variants)})
            """;
        cmd.Parameters.Add(new SqliteParameter("@marker", marker));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string VariantIdParams(SqliteCommand cmd, IReadOnlyList<ActualizationVariant> variants)
    {
        var idParams = new string[variants.Count];
        for (var p = 0; p < variants.Count; p++)
        {
            idParams[p] = "@vid" + p;
            cmd.Parameters.Add(new SqliteParameter(idParams[p], variants[p].VersionId));
        }
        return string.Join(", ", idParams);
    }
}
