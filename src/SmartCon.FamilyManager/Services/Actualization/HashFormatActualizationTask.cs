using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Actualization;

/// <summary>
/// CRITICAL actualization task (Id=<c>hash-v16</c>): recalculates stale
/// (format v1..v15 / NULL) content hashes to the FHV16 format
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
    /// the loadable hash, Issue #238 ADR-069; FHV12 — DEF section
    /// (definition wiring) + strengthened GEOM (centroid, face-kind
    /// histogram, edge lengths, resolved RGBA, visibility flags, nested
    /// instance placements), system FHV8 prefix bump, Issue #249 Phase 3;
    /// FHV13 — sketch-content isolation: GEOM2D counts only free 2D
    /// elements (sketch-owned curves/dimensions are 3D-form wiring,
    /// covered by GEOM), DEF/DIMS lists only labeled dimensions, Issue
    /// #249 manual-test follow-up; FHV14 — section autonomy: DEF/FORMS
    /// lists only bound forms, GEOM2D drops plane/dimension counts, the
    /// sketch-curve exclusion is restricted to GenericForm-owned sketches
    /// (free 2D lines stay counted), Issue #249 manual-test round 2;
    /// FHV15 — deterministic reference type: evaluated sections are
    /// measured at the first-Ordinal named type inside a rolled-back
    /// transaction, not at the saved current type, Issue #249
    /// manual-test round 3; FHV16 — negative-zero canonicalization in FormatCoord (phantom GEOM diffs on symmetric parts), Issue #249 manual-test round 4).
/// Owns the <c>hash_format_version</c> marker
/// semantics: NULL/1..11 pending, 12 current, -1/-2 terminal (unreadable /
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

    public override string Id => "hash-v16";
    public override int Order => 12;
    public override bool IsCritical => true;

    protected override string DetectionSql => $"""
        FROM catalog_versions cv
        JOIN catalog_items ci ON ci.id = cv.catalog_item_id
        WHERE (cv.hash_format_version IS NULL OR cv.hash_format_version NOT IN ({FamilyContentHashFormat.CurrentVersion}, -1, -2))
        """;

    public override async Task ApplyAsync(FamilyActualizationContext context, CancellationToken ct = default)
    {
        string? hash;
        IReadOnlyList<ContentSectionHash>? sections = null;
        SystemFamilySnapshot? trimmedSystem = null;
        if (context.SystemSnapshot is not null)
        {
            trimmedSystem = await SystemTypeCatalogTrimHelper.TrimToCatalogTypeNamesAsync(
                    context.SystemSnapshot, context.Group, Database, ct)
                .ConfigureAwait(false);
            hash = _contentHasher.ComputeForSystem(trimmedSystem)?.HexString;
            sections = hash is null ? null : _contentHasher.ComputeSectionsForSystem(trimmedSystem);
        }
        else
        {
            var (loadableHash, loadableSections) = ActualizationSectionComposer.ComputeLoadable(
                context, _contentHasher, _compositeComposer);
            hash = loadableHash?.HexString;
            sections = hash is null ? null : loadableSections;
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

        // Sections ride along in the same UPDATE (#249 follow-up): without
        // this, section-hashes-v1 could only DETECT the group after hash-v16
        // had stamped hash_format_version=12 — i.e. on a SECOND «Обновить
        // базу» run. Null sections (computation failed) leave the columns
        // NULL and the backstop task picks the group up on the next run.
        var hashesJson = sections is null ? null : ContentSectionJsonSerializer.SerializeHashes(sections);
        var stringsJson = sections is null ? null : ContentSectionJsonSerializer.SerializeStrings(sections);

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
                    SET content_hash = @hash, hash_format_version = {FamilyContentHashFormat.CurrentVersion},
                        section_hashes = @secHashes, section_strings = @secStrings
                    WHERE id IN ({VariantIdParams(cmd, context.Group.Variants)})
                    """;
                cmd.Parameters.Add(new SqliteParameter("@hash", hash));
                cmd.Parameters.Add(new SqliteParameter("@secHashes", (object?)hashesJson ?? DBNull.Value));
                cmd.Parameters.Add(new SqliteParameter("@secStrings", (object?)stringsJson ?? DBNull.Value));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (context.Group.IsActiveLabel)
            {
                using var itemCmd = connection.CreateCommand();
                itemCmd.Transaction = tx;
                itemCmd.CommandText = $"""
                    UPDATE catalog_items
                    SET content_hash = @hash, hash_format_version = {FamilyContentHashFormat.CurrentVersion}, updated_at_utc = @now
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
