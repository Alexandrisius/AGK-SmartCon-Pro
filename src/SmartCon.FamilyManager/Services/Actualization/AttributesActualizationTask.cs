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
/// </summary>
internal sealed class AttributesActualizationTask : IDatabaseActualizationTask
{
    private readonly LocalCatalogDatabase _database;
    private readonly IFamilyDataImportService _dataImportService;
    private readonly ISharedNestedFamilyRepository _sharedNestedRepository;

    public AttributesActualizationTask(
        LocalCatalogDatabase database,
        IFamilyDataImportService dataImportService,
        ISharedNestedFamilyRepository sharedNestedRepository)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _dataImportService = dataImportService ?? throw new ArgumentNullException(nameof(dataImportService));
        _sharedNestedRepository = sharedNestedRepository ?? throw new ArgumentNullException(nameof(sharedNestedRepository));
    }

    public string Id => "attributes-v1";
    public int Order => 20;
    public bool IsCritical => false;

    // Active-label variants missing extraction data (A) or carrying broken
    // values (B). Scope: loadable, ACTIVE version only (older versions are
    // history); system families excluded (staged .rvt may not exist).
    private const string DetectionSql = """
        FROM catalog_versions cv
        JOIN catalog_items ci ON ci.id = cv.catalog_item_id
        WHERE ci.family_source = 'loadable'
          AND cv.version_label = ci.current_version_label
          AND (NOT EXISTS(SELECT 1 FROM family_data_import_runs r
                           WHERE r.catalog_item_id = ci.id AND r.version_id = cv.id
                             AND r.status IN ('Succeeded', 'Partial'))
               OR NOT EXISTS(SELECT 1 FROM family_types t
                              WHERE t.catalog_item_id = ci.id AND t.version_id = cv.id)
               OR EXISTS(SELECT 1 FROM extracted_attribute_values v
                          WHERE v.catalog_item_id = ci.id AND v.version_id = cv.id
                            AND (v.value_text = 'READERROR'
                                 OR (v.storage_type = 'Double' AND v.status = 'Found' AND v.unit_type_id IS NULL))))
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
        // ALL pending groups, including newer-Revit-only — the engine
        // classifies openability and reports them as newer-pending.
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
        // Pending groups with NO openable variant (openability is checked
        // across all variants of the active label — the engine applies the
        // result to every variant). RequiredRevitVersion = minimum Revit
        // making ALL newer-only groups processable in one pass.
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

    public Task HandleGroupFailureAsync(
        ActualizationGroup group, ActualizationFailureKind kind, CancellationToken ct = default)
    {
        // No terminal marker: attributes stay pending and are retried on
        // the next run (transient extraction errors self-heal).
        return Task.CompletedTask;
    }

    private async Task UpdateCountersAsync(
        ActualizationGroup group, FamilySnapshot snapshot, CancellationToken ct)
    {
        using var connection = _database.CreateConnection();
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
