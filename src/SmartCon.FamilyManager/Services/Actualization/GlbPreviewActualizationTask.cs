using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Actualization;

/// <summary>
/// OPTIONAL actualization task (Id=<c>glb-v1</c>): backfills the
/// auto-extracted 3D GLB preview asset for the ACTIVE version label of
/// each loadable item (ADR-054). Detection: no <c>family_assets</c> row
/// with <c>asset_type='Model3D'</c> and the auto-preview description for
/// the label. The asset row clears its own detection once written.
/// Failure policy: no terminal marker — the criterion stays pending and
/// is retried on the next run (default base-class behaviour).
/// </summary>
internal sealed class GlbPreviewActualizationTask : SqlDetectionActualizationTaskBase
{
    private readonly IFamilyGeometryPipeline _geometryPipeline;

    public GlbPreviewActualizationTask(
        LocalCatalogDatabase database,
        IFamilyGeometryPipeline geometryPipeline)
        : base(database)
    {
        _geometryPipeline = geometryPipeline ?? throw new ArgumentNullException(nameof(geometryPipeline));
    }

    public override string Id => "glb-v1";
    public override int Order => 30;
    public override bool IsCritical => false;

    protected override string DetectionSql => """
        FROM catalog_versions cv
        JOIN catalog_items ci ON ci.id = cv.catalog_item_id
        WHERE ci.family_source = 'loadable'
          AND cv.version_label = ci.current_version_label
          AND NOT EXISTS(SELECT 1 FROM family_assets a
                          WHERE a.catalog_item_id = ci.id AND a.version_label = cv.version_label
                            AND a.asset_type = 'Model3D' AND a.description LIKE 'auto-extracted-preview:%')
        """;

    public override async Task ApplyAsync(FamilyActualizationContext context, CancellationToken ct = default)
    {
        // Geometry came from the same open session; when it failed (null)
        // the pipeline falls back to its own extraction pass.
        await _geometryPipeline.RunAsync(
            context.Geometry,
            context.AbsolutePath,
            context.Group.CatalogItemId,
            context.OpenedVariant.VersionId,
            context.Group.VersionLabel,
            context.Snapshot.FamilyName,
            ct).ConfigureAwait(false);
    }
}
