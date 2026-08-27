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
/// Detection: the version has rows in <c>family_types</c> (types exist)
/// but none in <c>family_type_hashes</c>. Typeless loadable families
/// legitimately have zero rows — they never match the detection because
/// they have no <c>family_types</c> rows either. Versions whose content
/// hash carries a terminal sentinel (-1/-2: missing/unreadable file) are
/// excluded — re-opening them is known to fail (ADR-050 §2).
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

    protected override string DetectionSql => """
        FROM catalog_versions cv
        JOIN catalog_items ci ON ci.id = cv.catalog_item_id
        WHERE COALESCE(cv.hash_format_version, 0) NOT IN (-1, -2)
          AND EXISTS(SELECT 1 FROM family_types ft WHERE ft.version_id = cv.id)
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
            SmartConLogger.Info(
                $"type-hashes-v1: wrote {entries.Count} per-type hash(es) for '{context.Group.ItemName}' " +
                $"({context.Group.VersionLabel}, {context.Group.Variants.Count} variant(s))");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}
