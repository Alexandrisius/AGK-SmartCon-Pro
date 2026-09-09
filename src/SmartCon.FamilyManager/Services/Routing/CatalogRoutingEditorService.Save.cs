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

internal sealed partial class CatalogRoutingEditorService
{
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
}
