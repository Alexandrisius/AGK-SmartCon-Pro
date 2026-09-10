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
}
