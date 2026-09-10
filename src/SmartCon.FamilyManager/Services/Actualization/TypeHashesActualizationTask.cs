using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Actualization;

/// <summary>
/// OPTIONAL actualization task (Id=<c>type-hashes-v1</c>, Issue #249,
/// Phase 2): backfills <c>family_type_hashes</c> for catalog versions
/// imported before the per-type hash engine existed (or imported through
/// legacy paths that carry no Prepare-time hashes, e.g. folder import).
/// Detection: the version has a NAMED row in <c>family_types</c> (a real
/// type exists) but none in <c>family_type_hashes</c>. Typeless loadable
/// families legitimately have zero per-type hashes: modern imports write
/// no <c>family_types</c> rows for them, and pre-FHV8 imports left a
/// phantom <c>&lt;default&gt;</c> row which the hash engine ignores
/// (FHV8/#209 — the unnamed default type is never hashed) — detection
/// excludes versions whose <c>family_types</c> rows are all such
/// phantoms, otherwise the group would be re-detected on every run
/// forever (owner repro 2026-08-30). Versions whose content hash carries
/// a terminal sentinel (-1/-2: missing/unreadable file) are excluded —
/// re-opening them is known to fail (ADR-050 §2).
/// <para>
/// The per-type hashes of a group are written for ALL its Revit variants
/// in one transaction (the content is identical across variants — same
/// rule as <see cref="HashFormatActualizationTask"/>). System groups are
/// trimmed to the catalog-authoritative type identities via
/// <see cref="SystemTypeCatalogTrimHelper"/> — the same trim the
/// identity-hash task applies, so the two never disagree about the
/// authoritative type set.
/// </para>
/// </summary>
internal sealed class TypeHashesActualizationTask : SqlDetectionActualizationTaskBase
{
    private readonly IFamilyContentHasher _contentHasher;

    public TypeHashesActualizationTask(
        LocalCatalogDatabase database,
        IFamilyContentHasher contentHasher)
        : base(database)
    {
        _contentHasher = contentHasher ?? throw new ArgumentNullException(nameof(contentHasher));
    }

    public override string Id => "type-hashes-v1";
    public override int Order => 70;
    public override bool IsCritical => false;

    protected override string DetectionSql => $"""
        FROM catalog_versions cv
        JOIN catalog_items ci ON ci.id = cv.catalog_item_id
        WHERE COALESCE(cv.hash_format_version, 0) NOT IN (-1, -2)
          AND EXISTS(SELECT 1 FROM family_types ft
                     WHERE ft.version_id = cv.id
                       AND ft.type_name <> '{FamilyTypeSnapshot.DefaultTypeName}'
                       AND ft.type_name <> '')
          AND NOT EXISTS(SELECT 1 FROM family_type_hashes fth WHERE fth.catalog_version_id = cv.id)
        """;

    public override async Task ApplyAsync(FamilyActualizationContext context, CancellationToken ct = default)
    {
        IReadOnlyList<FamilyTypeHashEntry>? entries;
        if (context.SystemSnapshot is not null)
        {
            var trimmed = await SystemTypeCatalogTrimHelper.TrimToCatalogTypeNamesAsync(
                    context.SystemSnapshot, context.Group, Database, ct)
                .ConfigureAwait(false);
            entries = _contentHasher.ComputePerTypeHashesForSystem(trimmed)
                ?.Select(FamilyTypeHashEntry.ForSystemType)
                .ToList();
        }
        else
        {
            entries = _contentHasher.ComputePerTypeHashesForLoadable(context.Snapshot)
                ?.Select(kvp => FamilyTypeHashEntry.ForLoadableType(kvp.Key, kvp.Value))
                .ToList();
        }

        if (entries is null)
        {
            SmartConLogger.Warn(
                $"Per-type hash computation returned null for '{context.OpenedVariant.FileName}' (item '{context.Group.ItemName}') " +
                $"[Action: группа останется pending и будет повторена при следующем прогоне; при повторении переимпортируйте семейство]");
            return;
        }

        // An EMPTY set with family_types rows present is either a legacy
        // typeless family (phantom '<default>' rows only — the extractor
        // skips the unnamed default type, FHV8/#209) or genuine data
        // drift (named rows the extraction no longer sees). Detection
        // already excludes the phantom-only shape; the defensive check
        // below keeps ApplyAsync self-consistent. Genuine drift:
        // invalidate the stale rows (DELETE) so nothing serves wrong
        // hashes, log a Warn instead of a success Info — the group is
        // re-detected on the next run and the drift stays visible.
        var isDriftedEmptySet = entries.Count == 0;
        if (isDriftedEmptySet)
        {
            if (await AreFamilyTypesPhantomOnlyAsync(context, ct).ConfigureAwait(false))
            {
                SmartConLogger.Info(
                    $"type-hashes-v1: '{context.Group.ItemName}' is a typeless legacy family " +
                    $"(phantom '{FamilyTypeSnapshot.DefaultTypeName}' rows only) — nothing to backfill");
                return;
            }

            SmartConLogger.Warn(
                $"Per-type hash computation returned an empty set for '{context.OpenedVariant.FileName}' " +
                $"(item '{context.Group.ItemName}') while family_types rows exist — invalidating stale rows. " +
                $"[Action: данные рассинхронизированы — переимпортируйте семейство для восстановления per-type хэшей]");
        }

        // One write for ALL Revit variants of the group (the content is
        // identical across variants) in a single transaction.
        using var connection = Database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var tx = connection.BeginTransaction();
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("o");
            foreach (var variant in context.Group.Variants)
            {
                using (var deleteCmd = connection.CreateCommand())
                {
                    deleteCmd.Transaction = tx;
                    deleteCmd.CommandText = "DELETE FROM family_type_hashes WHERE catalog_version_id = @versionId";
                    deleteCmd.Parameters.Add(new SqliteParameter("@versionId", variant.VersionId));
                    await deleteCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                foreach (var entry in entries)
                {
                    using var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = tx;
                    insertCmd.CommandText = """
                        INSERT OR REPLACE INTO family_type_hashes
                            (catalog_version_id, type_identity_key, type_name, type_hash, created_at_utc)
                        VALUES (@versionId, @identityKey, @typeName, @typeHash, @createdAtUtc)
                        """;
                    insertCmd.Parameters.Add(new SqliteParameter("@versionId", variant.VersionId));
                    insertCmd.Parameters.Add(new SqliteParameter("@identityKey", entry.TypeIdentityKey));
                    insertCmd.Parameters.Add(new SqliteParameter("@typeName", entry.TypeName));
                    insertCmd.Parameters.Add(new SqliteParameter("@typeHash", entry.HashHex));
                    insertCmd.Parameters.Add(new SqliteParameter("@createdAtUtc", now));
                    await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            tx.Commit();
            if (!isDriftedEmptySet)
            {
                SmartConLogger.Info(
                    $"type-hashes-v1: wrote {entries.Count} per-type hash(es) for '{context.Group.ItemName}' " +
                    $"({context.Group.VersionLabel}, {context.Group.Variants.Count} variant(s))");
            }
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private async Task<bool> AreFamilyTypesPhantomOnlyAsync(FamilyActualizationContext context, CancellationToken ct)
    {
        using var connection = Database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        foreach (var variant in context.Group.Variants)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT COUNT(*) FROM family_types
                WHERE version_id = @versionId
                  AND type_name <> '{FamilyTypeSnapshot.DefaultTypeName}'
                  AND type_name <> ''
                """;
            cmd.Parameters.Add(new SqliteParameter("@versionId", variant.VersionId));
            var namedCount = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            if (namedCount > 0) return false;
        }

        return true;
    }
}
