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
internal sealed class CatalogRoutingEditorService : IRoutingEditorService
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

    public async Task<IReadOnlyList<RoutingPartCandidate>> GetPartCandidatesAsync(
        int fittingCategoryId,
        IReadOnlyCollection<int> partTypeOrdinals,
        int connectorShapeBits = 0,
        int requiredShapeMask = 0,
        bool excludeMultiShape = false,
        CancellationToken ct = default)
    {
        var candidates = new List<(string Id, string Name, string? ValueKey, string? ValueDisplay)>();
        using (var connection = _database.CreateConnection())
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = connection.CreateCommand();
            // Connector-shape filter (owner stress test 2026-09-01, баг 8):
            // a flex round duct must never offer rectangular-only fittings —
            // Revit would silently reject them at sync. A candidate matches
            // when its connector_shape bitmask shares any host bit; items
            // without the fact (pre-actualization DB) pass unfiltered.
            // requiredShapeMask (multi-shape transition rows): the candidate
            // must carry ALL the listed bits — a purely rectangular
            // transition never serves a rect-to-round row. excludeMultiShape
            // (plain Transitions rows): multi-shape transitions live in
            // their own rows — m & (m-1) = 0 keeps single-shape masks only.
            cmd.CommandText = """
                SELECT ci.id, ci.name, ff.value_key, ff.value_display
                FROM catalog_items ci
                LEFT JOIN family_facts ff
                    ON ff.catalog_item_id = ci.id AND ff.fact_key = 'part_type'
                LEFT JOIN family_facts ff_shape
                    ON ff_shape.catalog_item_id = ci.id AND ff_shape.fact_key = 'connector_shape'
                WHERE ci.family_source = 'loadable'
                  AND ci.revit_category_id = @cat
                  AND ci.content_status = 'Active'
                  AND (@shapeBits = 0
                       OR ff_shape.value_key IS NULL
                       OR (ff_shape.value_key <> ''
                           AND (CAST(ff_shape.value_key AS INTEGER) & @shapeBits) <> 0))
                  AND (@requiredMask = 0
                       OR ff_shape.value_key IS NULL
                       OR (ff_shape.value_key <> ''
                           AND (CAST(ff_shape.value_key AS INTEGER) & @requiredMask) = @requiredMask))
                  AND (@excludeMulti = 0
                       OR ff_shape.value_key IS NULL
                       OR (ff_shape.value_key <> ''
                           AND (CAST(ff_shape.value_key AS INTEGER)
                                & (CAST(ff_shape.value_key AS INTEGER) - 1)) = 0))
                ORDER BY ci.name
                """;
            cmd.Parameters.Add(new SqliteParameter("@cat", fittingCategoryId));
            cmd.Parameters.Add(new SqliteParameter("@shapeBits", connectorShapeBits));
            cmd.Parameters.Add(new SqliteParameter("@requiredMask", requiredShapeMask));
            cmd.Parameters.Add(new SqliteParameter("@excludeMulti", excludeMultiShape ? 1 : 0));
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
                TypesOrVirtualFallback(c, typesByItem),
                c.ValueKey is not null
                && int.TryParse(c.ValueKey, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var partOrdinal)
                    ? partOrdinal
                    : null))
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

        var (currentRulesRaw, currentSettings) = await _routingRuleRepository
            .HasAnyForItemAsync(catalogItemId, ct).ConfigureAwait(false)
            ? await _routingRuleRepository.ReadForItemAsync(catalogItemId, ct).ConfigureAwait(false)
            : await _routingRuleRepository.ReadForCurrentVersionAsync(catalogItemId, ct).ConfigureAwait(false);

        // FHV21: the Segments group left the item-level channel (it is
        // per-version mini content now) — the editor NEVER writes it. Any
        // legacy Segments rows still in the item tables ride through the
        // replace verbatim so pre-FHV21 items keep their display/sync until
        // the backfill moves the data to the per-version store.
        var legacySegmentRows = currentRulesRaw
            .Where(r => SegmentRuleComposition.IsSegmentsGroup(r.GroupKey)).ToList();
        var currentRules = currentRulesRaw
            .Where(r => !SegmentRuleComposition.IsSegmentsGroup(r.GroupKey)).ToList();

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
            // Carry-over (audit M1): stored rules of groups the editor does
            // NOT show for this category (e.g. duct Segments/MechanicalJoints
            // captured by live extraction, or future param groups) are not
            // part of the editor input — dropping them would silently erase
            // routing that the next sync would then "converge" out of user
            // projects. Keep the stored rows of non-descriptor groups
            // verbatim (same protection the read-only Segments row gets).
            var visibleKeys = new HashSet<string>(
                RoutingGroupCatalog
                    .GetGroups(ctx.Value.HostCategoryId, WithFittingsOf(edited.FamilyKey))
                    .Select(d => d.GroupKey),
                StringComparer.Ordinal);
            var editedIdentity = TypeIdentity(edited);
            newRules.AddRange(currentRules.Where(r =>
                TypeIdentity(r.TypeName, r.FamilyKey) == editedIdentity
                && !visibleKeys.Contains(r.GroupKey)));
            newRules.AddRange(edited.Rules);
            newSettings.Add(new FamilyRoutingTypeSettings(
                edited.TypeName, edited.FamilyKey, edited.PreferredJunctionType));
        }

        // FHV21: a caller passing Segments rows in the editor input is
        // legacy — the editor does not own that group; drop them from the
        // merge so they neither corrupt the no-op compare nor get written.
        newRules = newRules
            .Where(r => !SegmentRuleComposition.IsSegmentsGroup(r.GroupKey))
            .ToList();

        // No-op guard (owner stress test 2026-09-01): an edit whose records
        // are identical to the stored ones (phantom row without a picked
        // part, criteria re-entered unchanged) must not rewrite the tables
        // and must not surface a stale re-check downstream.
        if (RulesEqual(currentRules, newRules) && SettingsEqual(currentSettings, newSettings))
        {
            SmartConLogger.Info("Routing save: no effective changes — write skipped");
            return new RoutingSaveResult(true, [], null, Changed: false);
        }

        // Legacy Segments rows rejoin the write set verbatim (see above).
        newRules.AddRange(legacySegmentRows);

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

    /// <summary>
    /// Semantic equality of two rule sets (multiset of canonical strings —
    /// rule order inside a group IS content, reordering must compare
    /// unequal). Avoid interpolated format specifiers here — this assembly
    /// references HelixToolkit/SharpDX transitively (CS1739 on net48, #97).
    /// </summary>
    private static bool RulesEqual(
        IReadOnlyList<FamilyRoutingRuleInfo> a, IReadOnlyList<FamilyRoutingRuleInfo> b)
    {
        if (a.Count != b.Count)
            return false;
        return Canonicalize(a.Select(CanonicalRule))
            .SequenceEqual(Canonicalize(b.Select(CanonicalRule)));
    }

    private static bool SettingsEqual(
        IReadOnlyList<FamilyRoutingTypeSettings> a, IReadOnlyList<FamilyRoutingTypeSettings> b)
    {
        if (a.Count != b.Count)
            return false;
        return Canonicalize(a.Select(s =>
                s.TypeName + "" + s.FamilyKey + "" + s.PreferredJunctionType
                    .ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .SequenceEqual(Canonicalize(b.Select(s =>
                s.TypeName + "" + s.FamilyKey + "" + s.PreferredJunctionType
                    .ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    private static string CanonicalRule(FamilyRoutingRuleInfo r)
    {
        var sb = new StringBuilder(96);
        sb.Append(r.TypeName).Append('');
        sb.Append(r.FamilyKey).Append('');
        sb.Append(r.GroupKey).Append('');
        sb.Append(r.RuleOrder.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('');
        sb.Append(r.PartName ?? string.Empty).Append('');
        sb.Append(r.Description ?? string.Empty);
        foreach (var c in r.Criteria)
        {
            sb.Append('').Append(c.CriterionType).Append(':');
            sb.Append(c.MinimumSize.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(':');
            sb.Append(c.MaximumSize.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static List<string> Canonicalize(IEnumerable<string> values)
        => values.OrderBy(s => s, StringComparer.Ordinal).ToList();

    /// <summary>Conduit/cable-tray "without Fittings" classes hide TEE/CROSS
    /// (ADR-072 §2.7) — the discriminator is the family key suffix.</summary>
    private static bool WithFittingsOf(string familyKey)
        => !familyKey.EndsWith(".WithoutFittings", StringComparison.Ordinal);

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
            // The actual deletion lock (ADR-067) lives in family_dependencies
            // of the ARCHIVED versions — those rows are written at every
            // import and survive (unlike the frozen V34 routing tables,
            // which post-World-B versions never populate — audit M10).
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM family_dependencies d
                    WHERE d.parent_catalog_item_id = @item
                      AND d.parent_version_id <> @currentVid
                      AND d.dependency_kind = 'routing'
                      AND substr(d.part_name, 1, length(@fam) + 1) = @fam || ':'
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
