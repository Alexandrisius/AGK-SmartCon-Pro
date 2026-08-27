using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Actualization;

/// <summary>
/// OPTIONAL actualization task (Id=<c>section-hashes-v1</c>, Issue #249,
/// Phase 4): backfills <c>catalog_versions.section_hashes</c> /
/// <c>section_strings</c> for versions whose section columns are still
/// NULL. Detection: the version's content hash is current (= <see
/// cref="FamilyContentHashFormat.CurrentVersion"/>) but <c>section_hashes</c>
/// is NULL. Since the #249 follow-up, <see cref="HashFormatActualizationTask"/>
/// writes sections inline with its recompute, so this task is the BACKSTOP:
/// versions whose inline section computation failed, and databases written
/// by intermediate builds. Loadable sections are composed with the
/// shared-nested closure (the same enriched snapshot as the identity
/// hash — composite-consistent NESTEDHASH); system groups are trimmed
/// via <see cref="SystemTypeCatalogTrimHelper"/>.
/// </summary>
internal sealed class SectionHashesActualizationTask : SqlDetectionActualizationTaskBase
{
    private readonly IFamilyContentHasher _contentHasher;
    private readonly CompositeFamilyHashComposer _compositeComposer;

    public SectionHashesActualizationTask(
        LocalCatalogDatabase database,
        IFamilyContentHasher contentHasher)
        : base(database)
    {
        _contentHasher = contentHasher ?? throw new ArgumentNullException(nameof(contentHasher));
        _compositeComposer = new CompositeFamilyHashComposer(contentHasher);
    }

    public override string Id => "section-hashes-v1";
    public override int Order => 80;
    public override bool IsCritical => false;

    protected override string DetectionSql => $"""
        FROM catalog_versions cv
        JOIN catalog_items ci ON ci.id = cv.catalog_item_id
        WHERE cv.hash_format_version = {FamilyContentHashFormat.CurrentVersion}
          AND cv.section_hashes IS NULL
        """;

    public override async Task ApplyAsync(FamilyActualizationContext context, CancellationToken ct = default)
    {
        IReadOnlyList<ContentSectionHash>? sections;
        if (context.SystemSnapshot is not null)
        {
            var trimmed = await SystemTypeCatalogTrimHelper.TrimToCatalogTypeNamesAsync(
                    context.SystemSnapshot, context.Group, Database, ct)
                .ConfigureAwait(false);
            sections = _contentHasher.ComputeSectionsForSystem(trimmed);
        }
        else
        {
            sections = ActualizationSectionComposer.ComputeLoadable(
                context, _contentHasher, _compositeComposer).Sections;
        }

        if (sections is null)
        {
            SmartConLogger.Warn(
                $"Section computation returned null for '{context.OpenedVariant.FileName}' (item '{context.Group.ItemName}') " +
                $"[Action: группа останется pending и будет повторена при следующем прогоне; при повторении переимпортируйте семейство]");
            return;
        }

        var hashesJson = ContentSectionJsonSerializer.SerializeHashes(sections);
        var stringsJson = ContentSectionJsonSerializer.SerializeStrings(sections);

        using var connection = Database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var tx = connection.BeginTransaction();
        try
        {
            foreach (var variant in context.Group.Variants)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE catalog_versions
                    SET section_hashes = @hashes, section_strings = @strings
                    WHERE id = @versionId
                    """;
                cmd.Parameters.Add(new SqliteParameter("@versionId", variant.VersionId));
                cmd.Parameters.Add(new SqliteParameter("@hashes", hashesJson));
                cmd.Parameters.Add(new SqliteParameter("@strings", stringsJson));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            tx.Commit();
            SmartConLogger.Info(
                $"section-hashes-v1: wrote {sections.Count} section(s) for '{context.Group.ItemName}' " +
                $"({context.Group.VersionLabel}, {context.Group.Variants.Count} variant(s))");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}
