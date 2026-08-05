using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Actualization;

/// <summary>
/// CRITICAL actualization task (Id=<c>hash-v6</c>): recalculates stale
/// (format v1..v5 / NULL) content hashes to the FHV6 format
/// (Issue #159, ADR-056; FHV4 — Issues #184/#179/#190, ADR-065; FHV5 —
/// wire settings graph, manual test 2026-08-04; FHV6 — deterministic
/// TYPES ordering tie-breaks, stress test 2026-08-05). Owns the
/// <c>hash_format_version</c> marker
/// semantics: NULL/1/2/3/4/5 pending, 6 current, -1/-2 terminal (unreadable /
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

    public override string Id => "hash-v6";
    public override int Order => 10;
    public override bool IsCritical => true;

    protected override string DetectionSql => """
        FROM catalog_versions cv
        JOIN catalog_items ci ON ci.id = cv.catalog_item_id
        WHERE (cv.hash_format_version IS NULL OR cv.hash_format_version NOT IN (6, -1, -2))
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
                    SET content_hash = @hash, hash_format_version = 6
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
                    SET content_hash = @hash, hash_format_version = 6, updated_at_utc = @now
                    WHERE id = @itemId
                    """;
                itemCmd.Parameters.Add(new SqliteParameter("@hash", hash));
                itemCmd.Parameters.Add(new SqliteParameter("@now", DateTimeOffset.UtcNow.ToString("o")));
                itemCmd.Parameters.Add(new SqliteParameter("@itemId", context.Group.CatalogItemId));
                await itemCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // FHV6 is a breaking data format (ADR-058): a database carrying
            // v6 hashes must not be WRITTEN by a plugin older than the FHV5
            // release — its dedup would silently downgrade/duplicate. Runtime
            // backfill of the forward-compatibility floor (schema-migration
            // backfill like V24 cannot work here: v6 rows appear only AFTER
            // this task runs). Monotonic: a HIGHER pre-existing floor (from
            // a newer plugin) is never lowered.
            using (var readCmd = connection.CreateCommand())
            {
                readCmd.Transaction = tx;
                readCmd.CommandText = "SELECT min_plugin_version FROM database_meta LIMIT 1";
                var current = await readCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                var currentText = current is null or DBNull ? null : Convert.ToString(current);

                var shouldBump = currentText is null
                    || !SemVersion.TryParse(currentText, out var existing)
                    || !SemVersion.TryParse(DbCompatibility.CurrentMinPluginVersion, out var floor)
                    || floor > existing;

                if (shouldBump)
                {
                    using var metaCmd = connection.CreateCommand();
                    metaCmd.Transaction = tx;
                    metaCmd.CommandText = "UPDATE database_meta SET min_plugin_version = @minVersion";
                    metaCmd.Parameters.Add(new SqliteParameter("@minVersion", DbCompatibility.CurrentMinPluginVersion));
                    await metaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
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
        var catalogTypes = await LoadCatalogTypeIdentitiesAsync(group, ct).ConfigureAwait(false);
        if (catalogTypes.Count == 0)
        {
            SmartConLogger.Debug(
                $"No catalog type names for '{group.ItemName}' ({group.VersionLabel}) — " +
                "hashing the staged type list as-is");
            return snapshot;
        }

        // #190/#191: match by full identity — the name alone collapses
        // same-named types of different system families. A catalog row
        // matches a staged type when the name is equal AND the family
        // tokens agree; legacy rows (no key, no name — pre-V26) match by
        // name only.
        var kept = snapshot.Types
            .Where(t => catalogTypes.Any(c =>
                string.Equals(c.Name, t.Name, StringComparison.Ordinal)
                && (c.FamilyKey is not null
                    ? string.Equals(c.FamilyKey, t.FamilyKey, StringComparison.OrdinalIgnoreCase)
                    : c.FamilyName is not null
                        ? string.Equals(c.FamilyName, t.FamilyName, StringComparison.OrdinalIgnoreCase)
                        : true)))
            .ToList();

        if (kept.Count == snapshot.Types.Count)
            return snapshot;

        if (kept.Count == 0)
        {
            SmartConLogger.Warn(
                $"Staged types of '{group.ItemName}' ({group.VersionLabel}) match none of the " +
                $"{catalogTypes.Count} catalog type names — hashing the full staged list. " +
                $"[Action: при расхождении дедупликации переимпортируйте категорию из проекта]");
            return snapshot;
        }

        SmartConLogger.Debug(
            $"Trimmed staged types for '{group.ItemName}' ({group.VersionLabel}): " +
            $"{snapshot.Types.Count} → {kept.Count} (catalog authoritative list)");
        return snapshot with { Types = kept };
    }

    private async Task<List<(string Name, string? FamilyKey, string? FamilyName)>> LoadCatalogTypeIdentitiesAsync(
        ActualizationGroup group, CancellationToken ct)
    {
        var rows = new List<(string Name, string? FamilyKey, string? FamilyName)>();
        using var connection = Database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT DISTINCT type_name, family_key, family_name FROM family_types
            WHERE catalog_item_id = @itemId
              AND version_id IN ({VariantIdParams(cmd, group.Variants)})
            """;
        cmd.Parameters.Add(new SqliteParameter("@itemId", group.CatalogItemId));
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0)) continue;
            var familyKey = reader.IsDBNull(1) || string.IsNullOrEmpty(reader.GetString(1)) ? null : reader.GetString(1);
            var familyName = reader.IsDBNull(2) || string.IsNullOrEmpty(reader.GetString(2)) ? null : reader.GetString(2);
            rows.Add((reader.GetString(0), familyKey, familyName));
        }
        return rows;
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
