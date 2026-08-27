using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Actualization;

/// <summary>
/// OPTIONAL actualization task (Id=<c>section-hashes-v1</c>, Issue #249,
/// Phase 4): backfills <c>catalog_versions.section_hashes</c> /
/// <c>section_strings</c> for versions imported before the content-
/// section analytics existed. Detection: the version's content hash is
/// current (= <see cref="FamilyContentHashFormat.CurrentVersion"/>) but
/// <c>section_hashes</c> is NULL — older-format versions are the
/// critical hash task's job (it runs first, Order 12 &lt; 80, and its
/// recompute does NOT write sections — this task then fills them in
/// the same engine pass). Loadable sections are composed with the
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
            sections = ComputeLoadableSections(context);
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

    /// <summary>
    /// Loadable sections of the group — composed with the shared-nested
    /// closure when the extraction carried it (same enriched snapshot as
    /// <see cref="HashFormatActualizationTask"/>'s identity hash, so the
    /// NESTEDHASH section is composite-consistent).
    /// </summary>
    private IReadOnlyList<ContentSectionHash>? ComputeLoadableSections(FamilyActualizationContext context)
    {
        if (context.SharedNestedSubtrees is not { Count: > 0 } subtrees)
        {
            return _contentHasher.ComputeSectionsForLoadable(context.Snapshot);
        }

        var snapshots = new Dictionary<string, FamilySnapshot>(StringComparer.OrdinalIgnoreCase);
        var rootName = FamilyNameNormalizer.Normalize(context.Snapshot.FamilyName);
        snapshots[rootName] = context.Snapshot;
        foreach (var nested in context.SharedNestedSnapshots ?? (IReadOnlyList<FamilySnapshot>)Array.Empty<FamilySnapshot>())
        {
            snapshots[FamilyNameNormalizer.Normalize(nested.FamilyName)] = nested;
        }

        var flatSubtrees = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var subtree in subtrees)
        {
            flatSubtrees[subtree.OwnerFamilyName] = subtree.NestedFamilyNames;
        }

        var composed = _compositeComposer.ComposeDetailed(snapshots, flatSubtrees);
        return composed.TryGetValue(rootName, out var result)
            ? result.Sections
            : _contentHasher.ComputeSectionsForLoadable(context.Snapshot);
    }
}
