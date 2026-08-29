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
/// Phase 3). Save clones the current version into a new one inside a single
/// transaction: version row, family_types copies, routing tables (V34 SQL
/// mirrors <see cref="LocalFamilyRoutingRuleRepository"/> so the rows land
/// atomically with the version they belong to — a version without its
/// routing rows would trigger the destructive legacy fallback in sync),
/// recomputed per-type hashes, canonical sections, the item's
/// current-version pointer and the regenerated family_dependencies links
/// (non-routing kinds carry over; routing links are rebuilt from the new
/// rules exactly like DependencyLinkWriter does at import).
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
        var (rules, settings) = await _routingRuleRepository
            .ReadForVersionAsync(catalogItemId, ctx.Value.VersionId, ct)
            .ConfigureAwait(false);

        var missingFamilies = new List<string>();
        foreach (var family in PartFamiliesOf(rules))
        {
            var child = await _catalog
                .FindByNormalizedNameAsync(FamilyNameNormalizer.Normalize(family), "loadable", ct)
                .ConfigureAwait(false);
            if (child is null)
                missingFamilies.Add(family);
        }

        var hasCollisions = types
            .GroupBy(t => t.TypeName, StringComparer.Ordinal)
            .Any(g => g.Count() > 1);

        var sizeNominals = await _segmentSizes
            .ReadDistinctNominalsAsync(ctx.Value.VersionId, ct)
            .ConfigureAwait(false);

        SmartConLogger.Info(
            $"Loaded routing editor data: types={types.Count}, rules={rules.Count}, " +
            $"settings={settings.Count}, missingParts={missingFamilies.Count}, legacy={ctx.Value.IsLegacy}, " +
            $"typeNameCollisions={hasCollisions}, sizeOptions={sizeNominals.Count}");
        return new RoutingEditorData(
            ctx.Value.HostCategoryId,
            ctx.Value.IsLegacy,
            hasCollisions,
            types,
            rules,
            settings,
            missingFamilies,
            sizeNominals);
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
                typesByItem.TryGetValue(c.Id, out var types) ? types : (IReadOnlyList<string>)[]))
            .ToList();
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
            return new RoutingSaveResult(false, null, [],
                "Item is not a system MEPCurve catalog item");
        }
        if (ctx.Value.IsLegacy || ctx.Value.SectionStrings is null)
        {
            SmartConLogger.Warn(
                "Routing save refused: legacy version without stored canonical sections. " +
                "[Action: выполните «Обновить базу» или переимпортируйте системную категорию — после этого редактирование станет доступно]");
            return new RoutingSaveResult(false, null, [], "LegacyVersion");
        }

        var types = await ReadTypesAsync(ctx.Value.VersionId, ct).ConfigureAwait(false);
        if (types.GroupBy(t => t.TypeName, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            // Same-named types of different system families in one item
            // (duct Round/Rect/Oval, conduit With/WithoutFittings) collide
            // on the name-keyed section keys of V33 — recomposition cannot
            // be byte-exact for them, so the editor refuses the save.
            SmartConLogger.Warn(
                "Routing save refused: the item has same-named types in different system families " +
                "(V33 section keys are name-keyed — recomposition would corrupt hashes). " +
                "[Action: переимпортируйте системную категорию для обновления хранилища секций]");
            return new RoutingSaveResult(false, null, [], "TypeNameCollisions");
        }
        var (currentRules, currentSettings) = await _routingRuleRepository
            .ReadForVersionAsync(catalogItemId, ctx.Value.VersionId, ct)
            .ConfigureAwait(false);

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
        var newRoutingByType = new Dictionary<string, RoutingPreferencesSnapshot?>(StringComparer.Ordinal);
        foreach (var edited in save.EditedTypes)
        {
            newRules.AddRange(edited.Rules);
            newSettings.Add(new FamilyRoutingTypeSettings(
                edited.TypeName, edited.FamilyKey, edited.PreferredJunctionType));
            newRoutingByType[edited.TypeName] = RoutingRuleRecordMapper.ToSnapshot(
                edited.TypeName, edited.FamilyKey, edited.Rules,
                [new FamilyRoutingTypeSettings(edited.TypeName, edited.FamilyKey, edited.PreferredJunctionType)]);
        }

        RecomposedSystemSections recomposed;
        try
        {
            recomposed = FamilyContentHasher.RebuildSystemSectionsWithRouting(
                ctx.Value.SectionStrings,
                types.Select(t => new RecomposeTypeIdentity(t.TypeName, t.FamilyKey, t.FamilyName)).ToList(),
                newRoutingByType);
        }
        catch (InvalidOperationException ex)
        {
            SmartConLogger.Warn(
                $"Routing save refused: {ex.Message} [Action: выполните «Обновить базу» или переимпортируйте системную категорию]");
            return new RoutingSaveResult(false, null, [], "LegacyVersion");
        }

        var archivedLocked = await FindArchivedLockedPartsAsync(
            catalogItemId, ctx.Value.VersionId, currentRules, newRules, ct).ConfigureAwait(false);

        var newVersionId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;

        using (var connection = _database.CreateConnection())
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var tx = connection.BeginTransaction();
            try
            {
                var newLabel = await ComputeNextVersionLabelAsync(connection, tx, catalogItemId, ct)
                    .ConfigureAwait(false);
                await InsertClonedVersionAsync(connection, tx, ctx.Value.VersionId, newVersionId,
                    catalogItemId, newLabel, recomposed, now, ct).ConfigureAwait(false);
                await CloneFamilyTypesAsync(connection, tx, ctx.Value.VersionId, newVersionId, ct)
                    .ConfigureAwait(false);
                await InsertRoutingRowsAsync(connection, tx, catalogItemId, newVersionId,
                    newRules, newSettings, ct).ConfigureAwait(false);
                await InsertTypeHashesAsync(connection, tx, newVersionId, recomposed.TypeHashes, now, ct)
                    .ConfigureAwait(false);
                await CopySegmentSizesAsync(connection, tx, ctx.Value.VersionId, newVersionId, ct)
                    .ConfigureAwait(false);
                var linksWritten = await RebuildDependencyLinksAsync(connection, tx, catalogItemId,
                    ctx.Value.VersionId, newVersionId, newRules, ct).ConfigureAwait(false);
                await UpdateItemPointerAsync(connection, tx, catalogItemId, newLabel,
                    recomposed.ContentHashHex, now, ct).ConfigureAwait(false);

                tx.Commit();
                SmartConLogger.Info(
                    $"Routing saved as {newLabel}: rules={newRules.Count}, settings={newSettings.Count}, " +
                    $"links={linksWritten}, archivedLocked={archivedLocked.Count}, " +
                    $"content_hash='{recomposed.ContentHashHex}'");
                return new RoutingSaveResult(true, newLabel, archivedLocked, null);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                SmartConLogger.Error($"Routing save FAILED (rolled back): {ex.GetType().Name}: {ex.Message}");
                return new RoutingSaveResult(false, null, [], ex.Message);
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

    private async Task<(int HostCategoryId, string VersionId, bool IsLegacy, IReadOnlyDictionary<string, string>? SectionStrings)?>
        ReadContextAsync(string catalogItemId, CancellationToken ct)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ci.family_source, ci.revit_category_id, cv.id, cv.section_strings
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

        var sectionStrings = ContentSectionJsonSerializer.Deserialize(
            reader.IsDBNull(3) ? null : reader.GetString(3));
        return (categoryId!.Value, reader.GetString(2), sectionStrings is null, sectionStrings);
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

    private static async Task<string> ComputeNextVersionLabelAsync(
        SqliteConnection connection, SqliteTransaction tx, string catalogItemId, CancellationToken ct)
    {
        // Scan EVERY label of the item and pick max(vN)+1 — the label set
        // is unique per item (UNIQUE(catalog_item_id, version_label, ...)),
        // so the result can never collide; picking "latest by published_at"
        // could (clock skew, manual edits) and crashed the save on the
        // unique constraint with a raw SQLite error.
        var maxNumber = 1;
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT version_label FROM catalog_versions WHERE catalog_item_id = @itemId";
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var label = reader.GetString(0);
            if (label.Length > 1 && label[0] == 'v'
#if NET8_0_OR_GREATER
                && int.TryParse(label.AsSpan(1), out var num))
#else
                && int.TryParse(label.Substring(1), out var num))
#endif
            {
                if (num > maxNumber)
                    maxNumber = num;
            }
        }
        return "v" + (maxNumber + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task InsertClonedVersionAsync(
        SqliteConnection connection, SqliteTransaction tx,
        string sourceVersionId, string newVersionId, string catalogItemId, string newLabel,
        RecomposedSystemSections recomposed, DateTimeOffset now, CancellationToken ct)
    {
        // Clone the current version's file/reference columns; the routing
        // edit does not touch the managed mini file (routing lives in the
        // DB, not in the slim mini — ADR-072). glb_state resets to NULL so
        // the glb-v1 actualization regenerates the 3D preview for the new
        // version instead of inheriting a state whose glb file belongs to
        // the source version. routing_backfilled=1: the editor writes the
        // routing rows in the same transaction — backfilled by construction.
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO catalog_versions
                (id, catalog_item_id, file_id, version_label, revit_major_version,
                 types_count, parameters_count, content_hash, hash_format_version,
                 glb_state, es_marker_version, routing_backfilled,
                 section_hashes, section_strings, published_at_utc, published_by)
            SELECT @newId, catalog_item_id, file_id, @label, revit_major_version,
                 types_count, parameters_count, @contentHash, hash_format_version,
                 NULL, es_marker_version, 1,
                 @sectionHashes, @sectionStrings, @publishedAt, published_by
            FROM catalog_versions
            WHERE id = @sourceId
            """;
        cmd.Parameters.Add(new SqliteParameter("@newId", newVersionId));
        cmd.Parameters.Add(new SqliteParameter("@label", newLabel));
        cmd.Parameters.Add(new SqliteParameter("@contentHash", recomposed.ContentHashHex));
        cmd.Parameters.Add(new SqliteParameter("@sectionHashes",
            ContentSectionJsonSerializer.SerializeHashes(recomposed.Sections)));
        cmd.Parameters.Add(new SqliteParameter("@sectionStrings",
            ContentSectionJsonSerializer.SerializeStrings(recomposed.Sections)));
        cmd.Parameters.Add(new SqliteParameter("@publishedAt", now.ToString("o")));
        cmd.Parameters.Add(new SqliteParameter("@sourceId", sourceVersionId));
        var inserted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (inserted != 1)
            throw new InvalidOperationException($"Clone of version '{sourceVersionId}' inserted {inserted} rows");
    }

    private static async Task CloneFamilyTypesAsync(
        SqliteConnection connection, SqliteTransaction tx,
        string sourceVersionId, string newVersionId, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO family_types
                (id, catalog_item_id, type_name, sort_order, version_id, file_id,
                 extraction_run_id, type_unique_id, family_name, family_key)
            SELECT lower(hex(randomblob(16))), catalog_item_id, type_name, sort_order, @newVid, file_id,
                 extraction_run_id, type_unique_id, family_name, family_key
            FROM family_types
            WHERE version_id = @sourceVid
            """;
        cmd.Parameters.Add(new SqliteParameter("@newVid", newVersionId));
        cmd.Parameters.Add(new SqliteParameter("@sourceVid", sourceVersionId));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Segment size tables ride along with the new version verbatim (a
    /// routing edit never changes segments) — INSERT..SELECT keeps the copy
    /// inside the save transaction.
    /// </summary>
    private static async Task CopySegmentSizesAsync(
        SqliteConnection connection, SqliteTransaction tx,
        string sourceVersionId, string newVersionId, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO family_segment_sizes
                (catalog_version_id, segment_name, nominal_diameter, inner_diameter,
                 outer_diameter, used_in_size_lists, used_in_sizing, sort_order)
            SELECT @newVid, segment_name, nominal_diameter, inner_diameter,
                 outer_diameter, used_in_size_lists, used_in_sizing, sort_order
            FROM family_segment_sizes
            WHERE catalog_version_id = @sourceVid
            """;
        cmd.Parameters.Add(new SqliteParameter("@newVid", newVersionId));
        cmd.Parameters.Add(new SqliteParameter("@sourceVid", sourceVersionId));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task InsertRoutingRowsAsync(
        SqliteConnection connection, SqliteTransaction tx,
        string catalogItemId, string newVersionId,
        IReadOnlyList<FamilyRoutingRuleInfo> rules,
        IReadOnlyList<FamilyRoutingTypeSettings> settings,
        CancellationToken ct)
    {
        foreach (var rule in rules)
        {
            ct.ThrowIfCancellationRequested();
            using var ins = connection.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO family_routing_rules
                    (catalog_item_id, catalog_version_id, family_key, type_name,
                     group_key, rule_order, part_name, description, criteria_json)
                VALUES (@item, @version, @famKey, @type, @group, @order, @part, @descr, @criteria)
                """;
            ins.Parameters.Add(new SqliteParameter("@item", catalogItemId));
            ins.Parameters.Add(new SqliteParameter("@version", newVersionId));
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
            using var ins = connection.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO family_routing_type_settings
                    (catalog_version_id, family_key, type_name, preferred_junction_type)
                VALUES (@version, @famKey, @type, @preferred)
                """;
            ins.Parameters.Add(new SqliteParameter("@version", newVersionId));
            ins.Parameters.Add(new SqliteParameter("@famKey", setting.FamilyKey));
            ins.Parameters.Add(new SqliteParameter("@type", setting.TypeName));
            ins.Parameters.Add(new SqliteParameter("@preferred", setting.PreferredJunctionType));
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task InsertTypeHashesAsync(
        SqliteConnection connection, SqliteTransaction tx,
        string newVersionId, IReadOnlyList<FamilyTypeHashEntry> entries,
        DateTimeOffset now, CancellationToken ct)
    {
        foreach (var entry in entries)
        {
            using var ins = connection.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT OR REPLACE INTO family_type_hashes
                    (catalog_version_id, type_identity_key, type_name, type_hash, created_at_utc)
                VALUES (@versionId, @identityKey, @typeName, @typeHash, @createdAtUtc)
                """;
            ins.Parameters.Add(new SqliteParameter("@versionId", newVersionId));
            ins.Parameters.Add(new SqliteParameter("@identityKey", entry.TypeIdentityKey));
            ins.Parameters.Add(new SqliteParameter("@typeName", entry.TypeName));
            ins.Parameters.Add(new SqliteParameter("@typeHash", entry.HashHex));
            ins.Parameters.Add(new SqliteParameter("@createdAtUtc", now.ToString("o")));
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Regenerates the links of the new version: non-routing kinds
    /// (shared_nested) carry over verbatim; routing links are rebuilt from
    /// the new rules with the same resolution DependencyLinkWriter applies
    /// at import (part family → loadable catalog item by normalized name;
    /// unresolved parts are legitimate presence flags, not warnings).
    /// </summary>
    private async Task<int> RebuildDependencyLinksAsync(
        SqliteConnection connection, SqliteTransaction tx,
        string catalogItemId, string sourceVersionId, string newVersionId,
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
            cmd.Parameters.Add(new SqliteParameter("@sourceVid", sourceVersionId));
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
            ins.Parameters.Add(new SqliteParameter("@version", newVersionId));
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

    private static async Task UpdateItemPointerAsync(
        SqliteConnection connection, SqliteTransaction tx,
        string catalogItemId, string newLabel, string contentHash,
        DateTimeOffset now, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE catalog_items
            SET current_version_label = @label, content_hash = @contentHash, updated_at_utc = @updatedAt
            WHERE id = @itemId
            """;
        cmd.Parameters.Add(new SqliteParameter("@label", newLabel));
        cmd.Parameters.Add(new SqliteParameter("@contentHash", contentHash));
        cmd.Parameters.Add(new SqliteParameter("@updatedAt", now.ToString("o")));
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
