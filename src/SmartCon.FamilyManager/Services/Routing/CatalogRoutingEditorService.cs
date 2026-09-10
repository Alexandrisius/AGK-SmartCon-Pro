using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Routing;

/// <summary>
/// SQLite implementation of <see cref="IRoutingEditorService"/> (ADR-072,
/// World B — owner decision 2026-08-29). Routing is a link between catalog
/// families, not file content: it lives in the item-level V37 tables,
/// outside the content hash and the version model. Save edits the links IN
/// PLACE inside one transaction (item tables + the current version's
/// regenerated <c>family_dependencies</c> routing links — non-routing kinds
/// carry over; routing links are rebuilt from the new rules exactly like
/// DependencyLinkWriter does at import). No version is created, no hash is
/// recomputed, no file is touched.
/// </summary>
internal sealed partial class CatalogRoutingEditorService : IRoutingEditorService
{
    private readonly LocalCatalogDatabase _database;
    private readonly IFamilyRoutingRuleRepository _routingRuleRepository;
    private readonly IFamilyCatalogProvider _catalog;
    private readonly ISegmentSizeRepository _segmentSizes;
    private readonly ISegmentRuleRepository _segmentRules;

    public CatalogRoutingEditorService(
        LocalCatalogDatabase database,
        IFamilyRoutingRuleRepository routingRuleRepository,
        IFamilyCatalogProvider catalog,
        ISegmentSizeRepository segmentSizes,
        ISegmentRuleRepository segmentRules)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _routingRuleRepository = routingRuleRepository ?? throw new ArgumentNullException(nameof(routingRuleRepository));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _segmentSizes = segmentSizes ?? throw new ArgumentNullException(nameof(segmentSizes));
        _segmentRules = segmentRules ?? throw new ArgumentNullException(nameof(segmentRules));
    }

    public async Task<RoutingEditorData?> LoadAsync(string catalogItemId, CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("RoutingEditor",
            ("Method", nameof(LoadAsync)),
            ("CatalogItemId", catalogItemId));

        var ctx = await ReadContextAsync(catalogItemId, ct).ConfigureAwait(false);
        if (ctx is null)
            return null;

        var types = await ReadTypesAsync(ctx.Value.VersionId, ct).ConfigureAwait(false);

        // Item-level links are the truth for FITTINGS; the current version's
        // V34 rows are the legacy fallback. Segment rules compose from the
        // PER-VERSION store (FHV21): the version pointer decides whose
        // ranges the tab shows — a rollback displays the activated version.
        var (storedRules, settings) = await _routingRuleRepository.HasAnyForItemAsync(catalogItemId, ct)
            .ConfigureAwait(false)
            ? await _routingRuleRepository.ReadForItemAsync(catalogItemId, ct).ConfigureAwait(false)
            : await _routingRuleRepository.ReadForCurrentVersionAsync(catalogItemId, ct).ConfigureAwait(false);
        var perVersionSegments = await _segmentRules
            .ReadForVersionAsync(ctx.Value.VersionId, ct).ConfigureAwait(false);
        var rules = SegmentRuleComposition.Compose(storedRules, perVersionSegments);

        var missingFamilies = new List<string>();
        var childIdByFamily = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var family in PartFamiliesOf(rules))
        {
            var child = await _catalog
                .FindByNormalizedNameAsync(FamilyNameNormalizer.Normalize(family), "loadable", ct)
                .ConfigureAwait(false);
            if (child is null)
                missingFamilies.Add(family);
            else
                childIdByFamily[family] = child.Id;
        }

        var partTypesByFamily = await ReadPartTypesAsync(childIdByFamily, ct).ConfigureAwait(false);

        // Size data exists for PIPES only (owner decision 2026-08-30):
        // duct/flex/conduit/cable-tray routing has no size conditions —
        // their groups must not receive size options or segment bounds.
        var hasSizeCriteria = RoutingGroupCatalog.HasSizeCriteria(ctx.Value.HostCategoryId);
        var sizeNominals = hasSizeCriteria
            ? await _segmentSizes
                .ReadDistinctNominalsAsync(ctx.Value.VersionId, ct)
                .ConfigureAwait(false)
            : Array.Empty<double>();

        var segmentBounds = hasSizeCriteria
            ? await ReadSegmentBoundsAsync(ctx.Value.VersionId, ct)
                .ConfigureAwait(false)
            : Array.Empty<SegmentSizeBounds>();

        SmartConLogger.Info(
            $"Loaded routing editor data: types={types.Count}, rules={rules.Count}, " +
            $"settings={settings.Count}, missingParts={missingFamilies.Count}, sizeOptions={sizeNominals.Count}, " +
            $"segmentBounds={segmentBounds.Count}");
        return new RoutingEditorData(
            ctx.Value.HostCategoryId,
            types,
            rules,
            settings,
            missingFamilies,
            sizeNominals,
            segmentBounds,
            partTypesByFamily);
    }

    /// <summary>Part-type ordinal per rule-part family (junctions grey-out).</summary>
    private async Task<IReadOnlyDictionary<string, int>> ReadPartTypesAsync(
        Dictionary<string, string> childIdByFamily, CancellationToken ct)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (childIdByFamily.Count == 0)
            return result;

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        var parameters = childIdByFamily.Values
            .Select((id, index) => (id, Name: "@p" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();
        cmd.CommandText = $"""
            SELECT catalog_item_id, value_key
            FROM family_facts
            WHERE fact_key = 'part_type'
              AND catalog_item_id IN ({string.Join(", ", parameters.Select(p => p.Name))})
            """;
        foreach (var p in parameters)
            cmd.Parameters.Add(new SqliteParameter(p.Name, p.id));
        var valueByChild = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(1))
                    valueByChild[reader.GetString(0)] = reader.GetString(1);
            }
        }

        foreach (var pair in childIdByFamily)
        {
            if (valueByChild.TryGetValue(pair.Value, out var valueKey)
                && int.TryParse(valueKey, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var ordinal))
            {
                result[pair.Key] = ordinal;
            }
        }

        return result;
    }

    /// <summary>
    /// Per-segment configured size span (min/max nominal diameter) — the
    /// read-only Segments row displays it like the Revit routing dialog.
    /// </summary>
    private async Task<IReadOnlyList<SegmentSizeBounds>> ReadSegmentBoundsAsync(
        string versionId, CancellationToken ct)
    {
        var result = new List<SegmentSizeBounds>();
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT segment_name, MIN(nominal_diameter), MAX(nominal_diameter)
            FROM family_segment_sizes
            WHERE catalog_version_id = @vid AND nominal_diameter > 0
            GROUP BY segment_name
            ORDER BY MIN(sort_order), segment_name
            """;
        cmd.Parameters.Add(new SqliteParameter("@vid", versionId));
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new SegmentSizeBounds(
                reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2)));
        }
        return result;
    }

    private async Task<(int HostCategoryId, string VersionId)?> ReadContextAsync(
        string catalogItemId, CancellationToken ct)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ci.family_source, ci.revit_category_id, cv.id
            FROM catalog_items ci
            INNER JOIN catalog_versions cv
                ON cv.catalog_item_id = ci.id AND cv.version_label = ci.current_version_label
            WHERE ci.id = @itemId
            LIMIT 1
            """;
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var familySource = reader.GetString(0);
        var categoryId = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
        if (!string.Equals(familySource, "system", StringComparison.Ordinal)
            || !RoutingGroupCatalog.IsMepCurveCategory(categoryId))
        {
            return null;
        }

        return (categoryId!.Value, reader.GetString(2));
    }

    private async Task<IReadOnlyList<RoutingEditorTypeData>> ReadTypesAsync(string versionId, CancellationToken ct)
    {
        var types = new List<RoutingEditorTypeData>();
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT type_name, family_key, family_name
            FROM family_types
            WHERE version_id = @vid
            ORDER BY sort_order, type_name
            """;
        cmd.Parameters.Add(new SqliteParameter("@vid", versionId));
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var familyKey = reader.GetString(1);
            types.Add(new RoutingEditorTypeData(
                reader.GetString(0),
                familyKey,
                reader.GetString(2),
                WithFittingsOf(familyKey)));
        }
        return types;
    }

    private async Task<Dictionary<string, IReadOnlyList<string>>> ReadCurrentVersionTypesAsync(
        IReadOnlyList<string> itemIds, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        foreach (var itemId in itemIds)
        {
            var types = new List<string>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT ft.type_name
                FROM family_types ft
                INNER JOIN catalog_items ci ON ci.id = ft.catalog_item_id
                INNER JOIN catalog_versions cv
                    ON cv.id = ft.version_id AND cv.version_label = ci.current_version_label
                WHERE ft.catalog_item_id = @itemId
                ORDER BY ft.sort_order, ft.type_name
                """;
            cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                types.Add(reader.GetString(0));
            }
            result[itemId] = types;
        }
        return result;
    }

    /// <summary>
    /// Regenerates the CURRENT version's links in place: non-routing kinds
    /// (shared_nested) carry over verbatim; routing links are deleted and
    /// rebuilt from the new rules with the same resolution
    /// DependencyLinkWriter applies at import (part family → loadable
    /// catalog item by normalized name; unresolved parts are legitimate
    /// presence flags, not warnings). Shared implementation:
    /// <see cref="RoutingDependencyLinkRebuilder"/>.
    /// </summary>
    private async Task<int> RebuildDependencyLinksAsync(
        SqliteConnection connection, SqliteTransaction tx,
        string catalogItemId, string currentVersionId,
        IReadOnlyList<FamilyRoutingRuleInfo> newRules, CancellationToken ct)
        => await RoutingDependencyLinkRebuilder
            .RebuildAsync(connection, tx, _catalog, catalogItemId, currentVersionId, newRules, ct)
            .ConfigureAwait(false);
    public async Task<IReadOnlyList<RoutingPhantomInfo>> FindRoutingPhantomsAsync(CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("RoutingEditor",
            ("Method", nameof(FindRoutingPhantomsAsync)));

        var references = await _routingRuleRepository
            .ReadAllPartReferencesAsync(ct)
            .ConfigureAwait(false);

        // Same resolution as the editor tab (PartFamiliesOf): family = the
        // part token before the first ':'; the manager-group rows are not
        // part references. Resolve each DISTINCT family name once.
        var missingByName = new Dictionary<string, bool>(StringComparer.Ordinal);
        var phantoms = new List<RoutingPhantomInfo>();
        foreach (var reference in references)
        {
            var separator = reference.PartName.IndexOf(':');
            if (separator <= 0) continue;
            var familyName = reference.PartName.Substring(0, separator);

            if (!missingByName.TryGetValue(familyName, out var isMissing))
            {
                var child = await _catalog
                    .FindByNormalizedNameAsync(FamilyNameNormalizer.Normalize(familyName), "loadable", ct)
                    .ConfigureAwait(false);
                isMissing = child is null;
                missingByName[familyName] = isMissing;
            }
            if (isMissing)
            {
                phantoms.Add(new RoutingPhantomInfo(
                    reference.CatalogItemId,
                    reference.FamilyKey,
                    reference.TypeName,
                    familyName));
            }
        }

        if (phantoms.Count > 0)
        {
            SmartConLogger.Info(
                $"Routing phantoms detected: rules={references.Count}, phantomRules={phantoms.Count} " +
                $"across {phantoms.Select(p => p.CatalogItemId).Distinct().Count()} families, " +
                $"missing families: {string.Join(", ", missingByName.Where(kv => kv.Value).Select(kv => kv.Key))}");
        }
        return phantoms;
    }
}
