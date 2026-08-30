using System.Text.Json;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
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
internal sealed class CatalogRoutingEditorService : IRoutingEditorService
{
    private readonly LocalCatalogDatabase _database;
    private readonly IFamilyRoutingRuleRepository _routingRuleRepository;
    private readonly IFamilyCatalogProvider _catalog;
    private readonly ISegmentSizeRepository _segmentSizes;

    public CatalogRoutingEditorService(
        LocalCatalogDatabase database,
        IFamilyRoutingRuleRepository routingRuleRepository,
        IFamilyCatalogProvider catalog,
        ISegmentSizeRepository segmentSizes)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _routingRuleRepository = routingRuleRepository ?? throw new ArgumentNullException(nameof(routingRuleRepository));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _segmentSizes = segmentSizes ?? throw new ArgumentNullException(nameof(segmentSizes));
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

        // Item-level links are the truth; the current version's V34 rows are
        // the legacy fallback until import/backfill seeds the item tables.
        var (rules, settings) = await _routingRuleRepository.HasAnyForItemAsync(catalogItemId, ct)
            .ConfigureAwait(false)
            ? await _routingRuleRepository.ReadForItemAsync(catalogItemId, ct).ConfigureAwait(false)
            : await _routingRuleRepository.ReadForCurrentVersionAsync(catalogItemId, ct).ConfigureAwait(false);

        var missingFamilies = new List<string>();
        foreach (var family in PartFamiliesOf(rules))
        {
            var child = await _catalog
                .FindByNormalizedNameAsync(FamilyNameNormalizer.Normalize(family), "loadable", ct)
                .ConfigureAwait(false);
            if (child is null)
                missingFamilies.Add(family);
        }

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
            segmentBounds);
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

    public async Task<IReadOnlyList<RoutingPartCandidate>> GetPartCandidatesAsync(
        int fittingCategoryId,
        IReadOnlyCollection<int> partTypeOrdinals,
        CancellationToken ct = default)
    {
        var candidates = new List<(string Id, string Name, string? ValueKey, string? ValueDisplay)>();
        using (var connection = _database.CreateConnection())
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT ci.id, ci.name, ff.value_key, ff.value_display
                FROM catalog_items ci
                LEFT JOIN family_facts ff
                    ON ff.catalog_item_id = ci.id AND ff.fact_key = 'part_type'
                WHERE ci.family_source = 'loadable'
                  AND ci.revit_category_id = @cat
                  AND ci.content_status = 'Active'
                ORDER BY ci.name
                """;
            cmd.Parameters.Add(new SqliteParameter("@cat", fittingCategoryId));
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                candidates.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        // Strict part_type filter (ADR-072 §2.7 item 4): when the group
        // defines ordinals, an item must carry a matching part_type fact —
        // param.Set/AddRule reject incompatible parts, so offering them
        // would produce a runtime rejection at sync.
        var filtered = partTypeOrdinals.Count == 0
            ? candidates
            : candidates.Where(c =>
                c.ValueKey is not null
                && int.TryParse(c.ValueKey, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var ordinal)
                && partTypeOrdinals.Contains(ordinal)).ToList();
        if (filtered.Count == 0)
            return [];

        var typesByItem = await ReadCurrentVersionTypesAsync(
            filtered.Select(c => c.Id).ToList(), ct).ConfigureAwait(false);

        return filtered
            .Select(c => new RoutingPartCandidate(
                c.Id,
                c.Name,
                c.ValueKey is not null
                    ? PartTypeLabelMap.TryGetLabel(c.ValueKey) ?? c.ValueDisplay
                    : null,
                TypesOrVirtualFallback(c, typesByItem)))
            .ToList();
    }

    /// <summary>
    /// A loadable family whose developer never named a type still loads into
    /// Revit with a default type named after the family (Revit auto-creates
    /// it), and the catalog tree shows the same virtual fallback. The picker
    /// must offer that virtual type so routing rules can reference the
    /// family — "Family:FamilyName" is exactly the token Revit's routing
    /// manager holds after placement.
    /// </summary>
    private static IReadOnlyList<string> TypesOrVirtualFallback(
        (string Id, string Name, string? ValueKey, string? ValueDisplay) candidate,
        IReadOnlyDictionary<string, IReadOnlyList<string>> typesByItem)
    {
        if (typesByItem.TryGetValue(candidate.Id, out var types) && types.Count > 0)
        {
            return types;
        }
        return new[] { candidate.Name };
    }

    public async Task<RoutingSaveResult> SaveAsync(
        string catalogItemId, RoutingEditorSave save, CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("RoutingEditor",
            ("Method", nameof(SaveAsync)),
            ("CatalogItemId", catalogItemId),
            ("EditedTypes", save.EditedTypes.Count));

        var ctx = await ReadContextAsync(catalogItemId, ct).ConfigureAwait(false);
        if (ctx is null)
        {
            return new RoutingSaveResult(false, [],
                "Item is not a system MEPCurve catalog item");
        }

        var (currentRules, currentSettings) = await _routingRuleRepository
            .HasAnyForItemAsync(catalogItemId, ct).ConfigureAwait(false)
            ? await _routingRuleRepository.ReadForItemAsync(catalogItemId, ct).ConfigureAwait(false)
            : await _routingRuleRepository.ReadForCurrentVersionAsync(catalogItemId, ct).ConfigureAwait(false);

        // Merge: untouched types keep their stored rules/settings verbatim;
        // edited types are replaced by the editor input.
        var editedKeys = new HashSet<string>(
            save.EditedTypes.Select(TypeIdentity),
            StringComparer.Ordinal);
        var newRules = currentRules
            .Where(r => !editedKeys.Contains(TypeIdentity(r.TypeName, r.FamilyKey)))
            .ToList();
        var newSettings = currentSettings
            .Where(s => !editedKeys.Contains(TypeIdentity(s.TypeName, s.FamilyKey)))
            .ToList();
        foreach (var edited in save.EditedTypes)
        {
            newRules.AddRange(edited.Rules);
            newSettings.Add(new FamilyRoutingTypeSettings(
                edited.TypeName, edited.FamilyKey, edited.PreferredJunctionType));
        }

        var archivedLocked = await FindArchivedLockedPartsAsync(
            catalogItemId, ctx.Value.VersionId, currentRules, newRules, ct).ConfigureAwait(false);

        using (var connection = _database.CreateConnection())
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var tx = connection.BeginTransaction();
            try
            {
                await ReplaceItemRoutingAsync(connection, tx, catalogItemId,
                    newRules, newSettings, ct).ConfigureAwait(false);
                var linksWritten = await RebuildDependencyLinksAsync(connection, tx,
                    catalogItemId, ctx.Value.VersionId, newRules, ct).ConfigureAwait(false);

                tx.Commit();
                SmartConLogger.Info(
                    $"Routing saved in place (no version): rules={newRules.Count}, settings={newSettings.Count}, " +
                    $"links={linksWritten}, archivedLocked={archivedLocked.Count}");
                return new RoutingSaveResult(true, archivedLocked, null);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                SmartConLogger.Error($"Routing save FAILED (rolled back): {ex.GetType().Name}: {ex.Message}");
                return new RoutingSaveResult(false, [], ex.Message);
            }
        }
    }

    private static string TypeIdentity(RoutingEditorTypeSave t) => TypeIdentity(t.TypeName, t.FamilyKey);
    private static string TypeIdentity(string typeName, string familyKey)
        => familyKey + "|" + typeName;

    private static IEnumerable<string> PartFamiliesOf(IReadOnlyList<FamilyRoutingRuleInfo> rules)
        => rules
            .Where(r => r.PartName is not null && r.GroupKey != RoutingGroupKeys.ForManagerGroup(0))
            .Select(r => r.PartName!)
            .Distinct(StringComparer.Ordinal)
            .Select(partName =>
            {
                var separator = partName.IndexOf(':');
                return separator > 0 ? partName.Substring(0, separator) : null;
            })
            .Where(f => f is not null)!;

    /// <summary>
    /// Part families the edit REMOVED from routing that archived versions
    /// of the item still reference — they stay dependency-locked (ADR-067)
    /// and the editor surfaces the hint so the user is not surprised the
    /// fitting family cannot be deleted yet.
    /// </summary>
    private async Task<List<string>> FindArchivedLockedPartsAsync(
        string catalogItemId,
        string currentVersionId,
        IReadOnlyList<FamilyRoutingRuleInfo> currentRules,
        IReadOnlyList<FamilyRoutingRuleInfo> newRules,
        CancellationToken ct)
    {
        var removed = PartFamiliesOf(currentRules)
            .Except(PartFamiliesOf(newRules), StringComparer.Ordinal)
            .ToList();
        if (removed.Count == 0)
            return [];

        var locked = new List<string>();
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        foreach (var family in removed)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM family_routing_rules r
                    WHERE r.catalog_item_id = @item
                      AND r.catalog_version_id <> @currentVid
                      AND substr(r.part_name, 1, length(@fam) + 1) = @fam || ':'
                    LIMIT 1)
                """;
            cmd.Parameters.Add(new SqliteParameter("@item", catalogItemId));
            cmd.Parameters.Add(new SqliteParameter("@currentVid", currentVersionId));
            cmd.Parameters.Add(new SqliteParameter("@fam", family));
            var exists = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (exists is long value && value != 0)
                locked.Add(family!);
        }
        return locked;
    }

    private static async Task ReplaceItemRoutingAsync(
        SqliteConnection connection, SqliteTransaction tx,
        string catalogItemId,
        IReadOnlyList<FamilyRoutingRuleInfo> rules,
        IReadOnlyList<FamilyRoutingTypeSettings> settings,
        CancellationToken ct)
    {
        using (var del = connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM item_routing_rules WHERE catalog_item_id = @itemId";
            del.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
            await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            del.CommandText = "DELETE FROM item_routing_type_settings WHERE catalog_item_id = @itemId";
            await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var rule in rules)
        {
            ct.ThrowIfCancellationRequested();
            using var ins = connection.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO item_routing_rules
                    (catalog_item_id, family_key, type_name, group_key, rule_order,
                     part_name, description, criteria_json)
                VALUES (@item, @famKey, @type, @group, @order, @part, @descr, @criteria)
                """;
            ins.Parameters.Add(new SqliteParameter("@item", catalogItemId));
            ins.Parameters.Add(new SqliteParameter("@famKey", rule.FamilyKey));
            ins.Parameters.Add(new SqliteParameter("@type", rule.TypeName));
            ins.Parameters.Add(new SqliteParameter("@group", rule.GroupKey));
            ins.Parameters.Add(new SqliteParameter("@order", rule.RuleOrder));
            ins.Parameters.Add(new SqliteParameter("@part", (object?)rule.PartName ?? DBNull.Value));
            ins.Parameters.Add(new SqliteParameter("@descr", rule.Description));
            ins.Parameters.Add(new SqliteParameter("@criteria", JsonSerializer.Serialize(rule.Criteria)));
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var setting in settings)
        {
            ct.ThrowIfCancellationRequested();
            using var ins = connection.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO item_routing_type_settings
                    (catalog_item_id, family_key, type_name, preferred_junction_type)
                VALUES (@item, @famKey, @type, @preferred)
                """;
            ins.Parameters.Add(new SqliteParameter("@item", catalogItemId));
            ins.Parameters.Add(new SqliteParameter("@famKey", setting.FamilyKey));
            ins.Parameters.Add(new SqliteParameter("@type", setting.TypeName));
            ins.Parameters.Add(new SqliteParameter("@preferred", setting.PreferredJunctionType));
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
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
                !familyKey.EndsWith(".WithoutFittings", StringComparison.Ordinal)));
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
    /// presence flags, not warnings).
    /// </summary>
    private async Task<int> RebuildDependencyLinksAsync(
        SqliteConnection connection, SqliteTransaction tx,
        string catalogItemId, string currentVersionId,
        IReadOnlyList<FamilyRoutingRuleInfo> newRules, CancellationToken ct)
    {
        var carryOver = new List<(string ChildId, string Kind, string? PartName, string? ChildVersionLabel)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT child_catalog_item_id, dependency_kind, part_name, child_version_label
                FROM family_dependencies
                WHERE parent_version_id = @sourceVid
                ORDER BY ordinal
                """;
            cmd.Parameters.Add(new SqliteParameter("@sourceVid", currentVersionId));
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var kind = reader.GetString(1);
                if (string.Equals(kind, FamilyDependencyKind.Routing, StringComparison.Ordinal))
                    continue;
                carryOver.Add((
                    reader.GetString(0),
                    kind,
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        using (var delRouting = connection.CreateCommand())
        {
            delRouting.Transaction = tx;
            delRouting.CommandText = """
                DELETE FROM family_dependencies
                WHERE parent_version_id = @version AND dependency_kind = 'routing'
                """;
            delRouting.Parameters.Add(new SqliteParameter("@version", currentVersionId));
            await delRouting.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var links = new List<FamilyDependencyInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var carried in carryOver)
        {
            if (!seen.Add(carried.ChildId + "|" + carried.Kind))
                continue;
            links.Add(new FamilyDependencyInfo(
                carried.ChildId, carried.Kind, carried.PartName, links.Count, carried.ChildVersionLabel));
        }

        var partNames = newRules
            .Where(r => r.PartName is not null && r.GroupKey != RoutingGroupKeys.ForManagerGroup(0))
            .Select(r => r.PartName!)
            .Distinct(StringComparer.Ordinal);
        foreach (var partName in partNames)
        {
            ct.ThrowIfCancellationRequested();
            var separator = partName.IndexOf(':');
            if (separator <= 0)
                continue;
            var familyName = partName.Substring(0, separator);
            var child = await _catalog
                .FindByNormalizedNameAsync(FamilyNameNormalizer.Normalize(familyName), "loadable", ct)
                .ConfigureAwait(false);
            if (child is null || !seen.Add(child.Id + "|" + FamilyDependencyKind.Routing))
                continue;
            links.Add(new FamilyDependencyInfo(
                child.Id, FamilyDependencyKind.Routing, partName, links.Count, child.CurrentVersionLabel));
        }

        foreach (var link in links)
        {
            using var ins = connection.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT OR REPLACE INTO family_dependencies
                    (parent_catalog_item_id, parent_version_id, child_catalog_item_id,
                     dependency_kind, part_name, ordinal, child_version_label)
                VALUES (@parent, @version, @child, @kind, @part, @ordinal, @childLabel)
                """;
            ins.Parameters.Add(new SqliteParameter("@parent", catalogItemId));
            ins.Parameters.Add(new SqliteParameter("@version", currentVersionId));
            ins.Parameters.Add(new SqliteParameter("@child", link.ChildCatalogItemId));
            ins.Parameters.Add(new SqliteParameter("@kind", link.Kind));
            ins.Parameters.Add(new SqliteParameter("@part", (object?)link.PartName ?? DBNull.Value));
            ins.Parameters.Add(new SqliteParameter("@ordinal", link.Ordinal));
            ins.Parameters.Add(new SqliteParameter("@childLabel",
                (object?)link.ChildVersionLabel ?? DBNull.Value));
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return links.Count;
    }
}
