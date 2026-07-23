using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Actualization;

/// <summary>
/// CRITICAL actualization task (Id=<c>hash-v3</c>): recalculates stale
/// (format v1 / v2 / NULL) content hashes to the FHV3 format
/// (Issue #159, ADR-056). Owns the <c>hash_format_version</c> marker
/// semantics: NULL/1/2 pending, 3 current, -1/-2 terminal (unreadable /
/// missing — never retried).
/// <para>
/// Unlike hash-v2, there is NO file-free pass: the FHV3 system canonical
/// string changed structurally (ordinal category, STRUCT, ROUTING), so
/// system rows need a full recomputation from the staged .rvt — the
/// engine opens it like any other file. Detection therefore covers BOTH
/// sources, and the base-class group counting is used unchanged.
/// </para>
/// <para>
/// System snapshots extracted from the staged mini-project may contain
/// template-default types of the default project (Phase-2 categories
/// without placed instances). <see cref="ApplyAsync"/> trims the type
/// list to the catalog's authoritative names from <c>family_types</c>
/// (written at import from the real project) so the migration hash
/// matches the import-time hash byte-for-byte.
/// </para>
/// </summary>
internal sealed class HashFormatActualizationTask : SqlDetectionActualizationTaskBase
{
    private readonly IFamilyContentHasher _contentHasher;

    public HashFormatActualizationTask(
        LocalCatalogDatabase database,
        IFamilyContentHasher contentHasher)
        : base(database)
    {
        _contentHasher = contentHasher ?? throw new ArgumentNullException(nameof(contentHasher));
    }

    public override string Id => "hash-v3";
    public override int Order => 10;
    public override bool IsCritical => true;

    protected override string DetectionSql => """
        FROM catalog_versions cv
        JOIN catalog_items ci ON ci.id = cv.catalog_item_id
        WHERE (cv.hash_format_version IS NULL OR cv.hash_format_version NOT IN (3, -1, -2))
        """;

    public override async Task ApplyAsync(FamilyActualizationContext context, CancellationToken ct = default)
    {
        string? hash;
        if (context.SystemSnapshot is not null)
        {
            var trimmed = await TrimToCatalogTypeNamesAsync(context.SystemSnapshot, context.Group, ct)
                .ConfigureAwait(false);
            hash = _contentHasher.ComputeForSystem(trimmed)?.HexString;
        }
        else
        {
            hash = _contentHasher.ComputeForLoadable(context.Snapshot)?.HexString;
        }

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
        using var connection = Database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var tx = connection.BeginTransaction();
        try
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"""
                    UPDATE catalog_versions
                    SET content_hash = @hash, hash_format_version = 3
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
                    SET content_hash = @hash, hash_format_version = 3, updated_at_utc = @now
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

    public override async Task HandleGroupFailureAsync(
        ActualizationGroup group, ActualizationFailureKind kind, CancellationToken ct = default)
    {
        // Terminal markers (ADR-050 §2): the hash criterion never retries —
        // -2 missing file, -1 unreadable file.
        var marker = kind == ActualizationFailureKind.MissingFile
            ? FamilyContentHashFormat.RecalculationMissing
            : FamilyContentHashFormat.RecalculationSkipped;
        await WriteMarkerAsync(group.Variants, marker, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Trim the staged-project type list to the catalog's authoritative
    /// type names (<c>family_types</c>, written at import from the real
    /// project). The staged .rvt of a Phase-2 category contains template
    /// defaults of the default project in addition to the copied types —
    /// hashing them would diverge from the import-time hash. When the
    /// catalog stores no type names for the group's versions (legacy
    /// data), the extractor's result is kept as-is (best effort). When
    /// names exist but match nothing (data drift), the full list is
    /// hashed with a warning — a mismatching hash degrades to the safe
    /// name-based dedup fallback instead of a terminal marker.
    /// </summary>
    private async Task<SystemFamilySnapshot> TrimToCatalogTypeNamesAsync(
        SystemFamilySnapshot snapshot, ActualizationGroup group, CancellationToken ct)
    {
        var catalogNames = await LoadCatalogTypeNamesAsync(group, ct).ConfigureAwait(false);
        if (catalogNames.Count == 0)
        {
            SmartConLogger.Debug(
                $"No catalog type names for '{group.ItemName}' ({group.VersionLabel}) — " +
                "hashing the staged type list as-is");
            return snapshot;
        }

        var kept = snapshot.Types
            .Where(t => catalogNames.Contains(t.Name))
            .ToList();

        if (kept.Count == snapshot.Types.Count)
            return snapshot;

        if (kept.Count == 0)
        {
            SmartConLogger.Warn(
                $"Staged types of '{group.ItemName}' ({group.VersionLabel}) match none of the " +
                $"{catalogNames.Count} catalog type names — hashing the full staged list. " +
                $"[Action: при расхождении дедупликации переимпортируйте категорию из проекта]");
            return snapshot;
        }

        SmartConLogger.Debug(
            $"Trimmed staged types for '{group.ItemName}' ({group.VersionLabel}): " +
            $"{snapshot.Types.Count} → {kept.Count} (catalog authoritative list)");
        return snapshot with { Types = kept };
    }

    private async Task<HashSet<string>> LoadCatalogTypeNamesAsync(
        ActualizationGroup group, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var connection = Database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT DISTINCT type_name FROM family_types
            WHERE catalog_item_id = @itemId
              AND version_id IN ({VariantIdParams(cmd, group.Variants)})
            """;
        cmd.Parameters.Add(new SqliteParameter("@itemId", group.CatalogItemId));
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0))
                names.Add(reader.GetString(0));
        }
        return names;
    }

    private async Task WriteMarkerAsync(
        IReadOnlyList<ActualizationVariant> variants, int marker, CancellationToken ct)
    {
        using var connection = Database.CreateConnection();
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
