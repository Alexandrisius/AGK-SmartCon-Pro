using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Actualization;

/// <summary>
/// OPTIONAL actualization task (Id=<c>attributes-v1</c>): backfills family
/// types + extracted attribute values + shared nested family names +
/// denormalized <c>types_count</c>/<c>parameters_count</c> for the ACTIVE
/// version of each loadable item (ADR-054). Detection covers both
/// "never extracted" (no terminal import run / no types — #152) and
/// "extracted broken" (<c>READERROR</c> — #153; raw units with
/// <c>unit_type_id IS NULL</c> — #151). Writes are idempotent replaces
/// (DELETE+INSERT per version), so re-processing is always safe.
/// Failure policy: no terminal marker — transient extraction errors
/// self-heal on the next run (default base-class behaviour).
/// </summary>
internal sealed class AttributesActualizationTask : SqlDetectionActualizationTaskBase
{
    private readonly IFamilyDataImportService _dataImportService;
    private readonly ISharedNestedFamilyRepository _sharedNestedRepository;

    public AttributesActualizationTask(
        LocalCatalogDatabase database,
        IFamilyDataImportService dataImportService,
        ISharedNestedFamilyRepository sharedNestedRepository)
        : base(database)
    {
        _dataImportService = dataImportService ?? throw new ArgumentNullException(nameof(dataImportService));
        _sharedNestedRepository = sharedNestedRepository ?? throw new ArgumentNullException(nameof(sharedNestedRepository));
    }

    public override string Id => "attributes-v1";
    public override int Order => 20;
    public override bool IsCritical => false;

    // Active-label variants missing extraction data (A) or carrying broken
    // values (B). Scope: loadable, ACTIVE version only (older versions are
    // history); system families excluded (staged .rvt may not exist).
    // The no-types clause fires only when a successful run CLAIMED types
    // (types_count > 0) yet the rows are gone: since FHV8 (#209) the
    // extractor always skips the phantom default type, so a phantom-only
    // family legitimately extracts ZERO named types — a Succeeded run with
    // types_count = 0 and no family_types rows is HEALTHY, not pending
    // (manual-test regression 2026-08-11: every phantom-only family lit
    // the amber "update recommended" dot forever).
    protected override string DetectionSql => """
        FROM catalog_versions cv
        JOIN catalog_items ci ON ci.id = cv.catalog_item_id
        WHERE ci.family_source = 'loadable'
          AND cv.version_label = ci.current_version_label
          AND (NOT EXISTS(SELECT 1 FROM family_data_import_runs r
                           WHERE r.catalog_item_id = ci.id AND r.version_id = cv.id
                             AND r.status IN ('Succeeded', 'Partial'))
               OR (NOT EXISTS(SELECT 1 FROM family_types t
                               WHERE t.catalog_item_id = ci.id AND t.version_id = cv.id)
                   AND EXISTS(SELECT 1 FROM family_data_import_runs r2
                               WHERE r2.catalog_item_id = ci.id AND r2.version_id = cv.id
                                 AND r2.status IN ('Succeeded', 'Partial')
                                 AND r2.types_count > 0))
               OR EXISTS(SELECT 1 FROM extracted_attribute_values v
                          WHERE v.catalog_item_id = ci.id AND v.version_id = cv.id
                            AND (v.value_text = 'READERROR'
                                 OR (v.storage_type = 'Double' AND v.status = 'Found' AND v.unit_type_id IS NULL))))
        """;

    public override async Task ApplyAsync(FamilyActualizationContext context, CancellationToken ct = default)
    {
        var snapshot = context.Snapshot;
        var extraction = SnapshotExtractionMapper.ToExtractionResult(
            snapshot, context.OpenedVariant.RevitMajorVersion);

        // Data is written to EVERY Revit variant of the label — the content
        // is identical across variants (same rule as the hash task).
        foreach (var variant in context.Group.Variants)
        {
            await _dataImportService.SaveExtractionResultAsync(
                context.Group.CatalogItemId, extraction, variant.VersionId, variant.FileId, ct)
                .ConfigureAwait(false);
            await _sharedNestedRepository.ReplaceForVersionAsync(
                context.Group.CatalogItemId, variant.VersionId, snapshot.SharedNestedFamilyNames, ct)
                .ConfigureAwait(false);
        }

        await UpdateCountersAsync(context.Group, snapshot, ct).ConfigureAwait(false);
    }

    private async Task UpdateCountersAsync(
        ActualizationGroup group, FamilySnapshot snapshot, CancellationToken ct)
    {
        using var connection = Database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        var idParams = new string[group.Variants.Count];
        for (var p = 0; p < group.Variants.Count; p++)
        {
            idParams[p] = "@vid" + p;
            cmd.Parameters.Add(new SqliteParameter(idParams[p], group.Variants[p].VersionId));
        }
        cmd.CommandText = $"""
            UPDATE catalog_versions
            SET types_count = @typesCount, parameters_count = @parametersCount
            WHERE id IN ({string.Join(", ", idParams)})
            """;
        cmd.Parameters.Add(new SqliteParameter("@typesCount", snapshot.Types.Count));
        cmd.Parameters.Add(new SqliteParameter("@parametersCount", snapshot.Parameters.Count));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
