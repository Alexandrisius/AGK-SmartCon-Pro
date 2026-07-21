using Microsoft.Data.Sqlite;
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
/// </summary>
internal sealed class GlbPreviewActualizationTask : IDatabaseActualizationTask
{
    private readonly LocalCatalogDatabase _database;
    private readonly IFamilyGeometryPipeline _geometryPipeline;

    public GlbPreviewActualizationTask(
        LocalCatalogDatabase database,
        IFamilyGeometryPipeline geometryPipeline)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _geometryPipeline = geometryPipeline ?? throw new ArgumentNullException(nameof(geometryPipeline));
    }

    public string Id => "glb-v1";
    public int Order => 30;
    public bool IsCritical => false;

    private const string DetectionSql = """
        FROM catalog_versions cv
        JOIN catalog_items ci ON ci.id = cv.catalog_item_id
        WHERE ci.family_source = 'loadable'
          AND cv.version_label = ci.current_version_label
          AND NOT EXISTS(SELECT 1 FROM family_assets a
                          WHERE a.catalog_item_id = ci.id AND a.version_label = cv.version_label
                            AND a.asset_type = 'Model3D' AND a.description LIKE 'auto-extracted-preview:%')
        """;

    public async Task<int> CountPendingAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*) FROM (
                SELECT cv.catalog_item_id, cv.version_label
                {DetectionSql}
                  AND cv.revit_major_version <= @maxRevit
                GROUP BY cv.catalog_item_id, cv.version_label
            )
            """;
        cmd.Parameters.Add(new SqliteParameter("@maxRevit", revitMajorVersion));
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long l ? (int)l : 0;
    }

    public async Task<IReadOnlyCollection<string>> LoadPendingGroupKeysAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT DISTINCT cv.catalog_item_id, cv.version_label
            {DetectionSql}
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            keys.Add(reader.GetString(0) + "|" + reader.GetString(1));
        }
        return keys;
    }

    public async Task<NewerOnlyPendingInfo> GetNewerOnlyPendingAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*), COALESCE(MAX((
                SELECT MIN(cv2.revit_major_version) FROM catalog_versions cv2
                WHERE cv2.catalog_item_id = p.itemId AND cv2.version_label = p.label)), 0)
            FROM (
                SELECT DISTINCT cv.catalog_item_id AS itemId, cv.version_label AS label
                {DetectionSql}
            ) p
            WHERE NOT EXISTS (
                SELECT 1 FROM catalog_versions cv3
                WHERE cv3.catalog_item_id = p.itemId AND cv3.version_label = p.label
                  AND cv3.revit_major_version <= @maxRevit
            )
            """;
        cmd.Parameters.Add(new SqliteParameter("@maxRevit", revitMajorVersion));
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return NewerOnlyPendingInfo.None;
        return new NewerOnlyPendingInfo(reader.GetInt32(0), reader.GetInt32(1));
    }

    public Task<int> RunFileFreePassAsync(int revitMajorVersion, CancellationToken ct = default)
        => Task.FromResult(0);

    public async Task ApplyAsync(FamilyActualizationContext context, CancellationToken ct = default)
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

    public Task HandleGroupFailureAsync(
        ActualizationGroup group, ActualizationFailureKind kind, CancellationToken ct = default)
    {
        // No terminal marker: the GLB criterion stays pending and is
        // retried on the next run.
        return Task.CompletedTask;
    }
}
