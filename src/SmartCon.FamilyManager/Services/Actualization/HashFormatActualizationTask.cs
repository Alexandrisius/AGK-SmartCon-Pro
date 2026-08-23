using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Actualization;

/// <summary>
/// CRITICAL actualization task (Id=<c>hash-v11</c>): recalculates stale
/// (format v1..v10 / NULL) content hashes to the FHV11 format
/// (Issue #159, ADR-056; FHV4 — Issues #184/#179/#190, ADR-065; FHV5 —
/// wire settings graph, manual test 2026-08-04; FHV6 — deterministic
/// TYPES ordering tie-breaks, stress test 2026-08-05; FHV7 — duct Shape
/// discriminator in FAMKEY, #215 manual test 2026-08-06; FHV8 —
/// composite shared-nested content in the loadable hash, #209 ADR-066;
/// FHV9 — PHANTOM section: parameter values of typeless families,
/// #209 stress test 2026-08-12; FHV10 — parameter groups leave the
/// loadable hash: the one content field no merge can transfer, and the
/// last difference between identity and embedded verification — one
/// unified hash now, owner decision 2026-08-12; FHV11 — LOOKUP section:
/// raw CSV content of embedded lookup tables (FamilySizeTable) enters
/// the loadable hash, Issue #238 ADR-069).
/// Owns the <c>hash_format_version</c> marker
/// semantics: NULL/1..10 pending, 11 current, -1/-2 terminal (unreadable /
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
/// <para>
/// FHV7 addition: the same Apply pass heals <c>family_types.family_key</c>
/// of system rows from the staged snapshot (duct "Single" → shape keys) —
/// without it the stored keys would break presence/stale matching until a
/// full re-import.
/// </para>
/// <para>
/// FHV8 addition (#209): loadable rows are composed with their shared-
/// nested closure — the extractor re-opens every nested family from the
/// managed .rfa itself (EditFamily, probe P2), so each group stays
/// self-contained (no cross-group ordering). A nested child whose own
/// catalog row is processed separately gets its own composite hash when
/// ITS group runs; both paths use the same
/// <see cref="CompositeFamilyHashComposer"/>.
/// </para>
/// </summary>
internal sealed class HashFormatActualizationTask : SqlDetectionActualizationTaskBase
{
    private readonly IFamilyContentHasher _contentHasher;
    private readonly CompositeFamilyHashComposer _compositeComposer;

    public HashFormatActualizationTask(
        LocalCatalogDatabase database,
        IFamilyContentHasher contentHasher)
        : base(database)
    {
        _contentHasher = contentHasher ?? throw new ArgumentNullException(nameof(contentHasher));
        _compositeComposer = new CompositeFamilyHashComposer(contentHasher);
    }

    public override string Id => "hash-v11";
    public override int Order => 11;
    public override bool IsCritical => true;

    protected override string DetectionSql => """
        FROM catalog_versions cv
        JOIN catalog_items ci ON ci.id = cv.catalog_item_id
        WHERE (cv.hash_format_version IS NULL OR cv.hash_format_version NOT IN (11, -1, -2))
        """;

    public override async Task ApplyAsync(FamilyActualizationContext context, CancellationToken ct = default)
    {
        string? hash;
        SystemFamilySnapshot? trimmedSystem = null;
        if (context.SystemSnapshot is not null)
        {
            trimmedSystem = await TrimToCatalogTypeNamesAsync(context.SystemSnapshot, context.Group, ct)
                .ConfigureAwait(false);
            hash = _contentHasher.ComputeForSystem(trimmedSystem)?.HexString;
        }
        else
        {
            hash = ComputeLoadableHash(context)?.HexString;
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
                    SET content_hash = @hash, hash_format_version = 11
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
                    SET content_hash = @hash, hash_format_version = 11, updated_at_utc = @now
                    WHERE id = @itemId
                    """;
                itemCmd.Parameters.Add(new SqliteParameter("@hash", hash));
                itemCmd.Parameters.Add(new SqliteParameter("@now", DateTimeOffset.UtcNow.ToString("o")));
                itemCmd.Parameters.Add(new SqliteParameter("@itemId", context.Group.CatalogItemId));
                await itemCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // FHV7 (#215): heal stored family_key of system rows from the
            // staged snapshot (duct "Single" → shape keys) — otherwise the
            // stored keys break presence/stale matching until a re-import.
            if (trimmedSystem is not null)
            {
                await HealFamilyKeysFromSnapshotAsync(connection, tx, trimmedSystem, context.Group, ct)
                    .ConfigureAwait(false);
            }

            // FHV8+ are breaking data formats (ADR-058): a database carrying
            // v11 hashes must not be WRITTEN by a plugin older than the FHV11
            // release — its dedup would silently downgrade/duplicate. Runtime
            // backfill of the forward-compatibility floor (schema-migration
            // backfill like V24 cannot work here: v10 rows appear only AFTER
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
        // name only. FHV7 (#215): a stored "Single" key is a LEGACY token —
        // for newly discriminated categories (ducts) it must not veto the
        // match against the staged shape key, otherwise every duct group
        // falls to the misleading "matches none" Warn and loses the trim.
        var kept = snapshot.Types
            .Where(t => catalogTypes.Any(c =>
                string.Equals(c.Name, t.Name, StringComparison.Ordinal)
                && (c.FamilyKey is not null && c.FamilyKey != SystemFamilyKeys.SingleFamily
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

    /// <summary>
    /// FHV7 (#215): rewrites <c>family_types.family_key</c> of the item's
    /// system rows from the staged snapshot (duct "Single" → shape keys).
    /// A row is matched by (type_name, family_name) — rows whose family_name
    /// is empty (loadable items, legacy pre-V26 rows) are never touched, and
    /// a type absent from the staged snapshot keeps its stored key (nothing
    /// better available). Runs inside the caller's transaction.
    /// </summary>
    private static async Task HealFamilyKeysFromSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        SystemFamilySnapshot snapshot,
        ActualizationGroup group,
        CancellationToken ct)
    {
        var updated = 0;
        foreach (var type in snapshot.Types)
        {
            if (string.IsNullOrEmpty(type.FamilyKey) || string.IsNullOrEmpty(type.FamilyName)) continue;
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE family_types
                SET family_key = @key
                WHERE catalog_item_id = @itemId
                  AND type_name = @name
                  AND family_name = @family
                  AND family_key <> @key
                """;
            cmd.Parameters.Add(new SqliteParameter("@key", type.FamilyKey));
            cmd.Parameters.Add(new SqliteParameter("@itemId", group.CatalogItemId));
            cmd.Parameters.Add(new SqliteParameter("@name", type.Name));
            cmd.Parameters.Add(new SqliteParameter("@family", type.FamilyName));
            updated += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (updated > 0)
        {
            SmartConLogger.Info(
                $"Healed family_key for {updated} family_types row(s) of '{group.ItemName}' ({group.VersionLabel}) from the staged snapshot (FHV7)");
        }
    }

    /// <summary>
    /// FHV8 (#209): composite hash for a loadable row. When the extraction
    /// carried the shared-nested closure (snapshots + flat subtree scans),
    /// the hash is composed bottom-up over the direct edges derived by
    /// subtraction; otherwise the plain own-content hash is computed
    /// (identical to a family without shared nested children).
    /// </summary>
    private FamilyContentHash? ComputeLoadableHash(FamilyActualizationContext context)
    {
        if (context.SharedNestedSubtrees is not { Count: > 0 } subtrees)
        {
            return _contentHasher.ComputeForLoadable(context.Snapshot);
        }

        var snapshots = new Dictionary<string, FamilySnapshot>(StringComparer.OrdinalIgnoreCase);
        var rootName = FamilyNameNormalizer.Normalize(context.Snapshot.FamilyName);
        snapshots[rootName] = context.Snapshot;
        foreach (var nested in context.SharedNestedSnapshots ?? (IReadOnlyList<FamilySnapshot>)Array.Empty<FamilySnapshot>())
        {
            // Last-wins on a normalized-name collision (same heuristic as
            // the import-time preparation queue).
            snapshots[FamilyNameNormalizer.Normalize(nested.FamilyName)] = nested;
        }

        var flatSubtrees = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var subtree in subtrees)
        {
            flatSubtrees[subtree.OwnerFamilyName] = subtree.NestedFamilyNames;
        }

        var composed = _compositeComposer.Compose(snapshots, flatSubtrees);
        return composed.TryGetValue(rootName, out var hash)
            ? hash
            : _contentHasher.ComputeForLoadable(context.Snapshot);
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
