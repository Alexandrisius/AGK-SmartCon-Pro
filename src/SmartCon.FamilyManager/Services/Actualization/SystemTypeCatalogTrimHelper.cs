using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Actualization;

/// <summary>
/// Shared helper for extraction-based actualization tasks that hash a
/// SYSTEM family group: trims the staged-project type list to the
/// catalog's authoritative type identities (<c>family_types</c>, written
/// at import from the real project). Extracted from
/// <see cref="HashFormatActualizationTask"/> (Issue #249, Phase 2) so the
/// <c>type-hashes-v1</c> backfill trims exactly like the identity-hash
/// task — the two must never disagree about the authoritative type set.
/// </summary>
internal static class SystemTypeCatalogTrimHelper
{
    /// <summary>
    /// Trim the staged-project type list to the catalog's authoritative
    /// type names (<c>family_types</c>, written at import from the real
    /// project). The staged .rvt of a Phase-2 category contains template
    /// defaults of the default project in addition to the copied types —
    /// hashing them would diverge from the import-time hash. When the
    /// catalog stores no type names for the group's versions (legacy
    /// data), the extractor's result is kept as-is (best effort). When
    /// names exist but match nothing (data drift), the full list is
    /// hashed with a warning — a mismatching hash degrades to the safe
    /// name-based dedup fallback instead of a terminal marker.
    /// </summary>
    public static async Task<SystemFamilySnapshot> TrimToCatalogTypeNamesAsync(
        SystemFamilySnapshot snapshot,
        ActualizationGroup group,
        LocalCatalogDatabase database,
        CancellationToken ct)
    {
        var catalogTypes = await LoadCatalogTypeIdentitiesAsync(group, database, ct).ConfigureAwait(false);
        if (catalogTypes.Count == 0)
        {
            SmartConLogger.Debug(
                $"No catalog type names for '{group.ItemName}' ({group.VersionLabel}) — " +
                "hashing the staged type list as-is");
            return snapshot;
        }

        // #190/#191: match by full identity — the name alone collapses
        // same-named types of different system families. A catalog row
        // matches a staged type when the name is equal AND the family
        // tokens agree; legacy rows (no key, no name — pre-V26) match by
        // name only. FHV7 (#215): a stored "Single" key is a LEGACY token —
        // for newly discriminated categories (ducts) it must not veto the
        // match against the staged shape key, otherwise every duct group
        // falls to the misleading "matches none" Warn and loses the trim.
        var kept = snapshot.Types
            .Where(t => catalogTypes.Any(c =>
                string.Equals(c.Name, t.Name, StringComparison.Ordinal)
                && (c.FamilyKey is not null && c.FamilyKey != SystemFamilyKeys.SingleFamily
                    ? string.Equals(c.FamilyKey, t.FamilyKey, StringComparison.OrdinalIgnoreCase)
                    : c.FamilyName is not null
                        ? string.Equals(c.FamilyName, t.FamilyName, StringComparison.OrdinalIgnoreCase)
                        : true)))
            .ToList();

        if (kept.Count == snapshot.Types.Count)
            return snapshot;

        if (kept.Count == 0)
        {
            SmartConLogger.Warn(
                $"Staged types of '{group.ItemName}' ({group.VersionLabel}) match none of the " +
                $"{catalogTypes.Count} catalog type names — hashing the full staged list. " +
                $"[Action: при расхождении дедупликации переимпортируйте категорию из проекта]");
            return snapshot;
        }

        SmartConLogger.Debug(
            $"Trimmed staged types for '{group.ItemName}' ({group.VersionLabel}): " +
            $"{snapshot.Types.Count} → {kept.Count} (catalog authoritative list)");
        return snapshot with { Types = kept };
    }

    private static async Task<List<(string Name, string? FamilyKey, string? FamilyName)>> LoadCatalogTypeIdentitiesAsync(
        ActualizationGroup group, LocalCatalogDatabase database, CancellationToken ct)
    {
        var rows = new List<(string Name, string? FamilyKey, string? FamilyName)>();
        using var connection = database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT DISTINCT type_name, family_key, family_name FROM family_types
            WHERE catalog_item_id = @itemId
              AND version_id IN ({VariantIdParams(cmd, group.Variants)})
            """;
        cmd.Parameters.Add(new SqliteParameter("@itemId", group.CatalogItemId));
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0)) continue;
            var familyKey = reader.IsDBNull(1) || string.IsNullOrEmpty(reader.GetString(1)) ? null : reader.GetString(1);
            var familyName = reader.IsDBNull(2) || string.IsNullOrEmpty(reader.GetString(2)) ? null : reader.GetString(2);
            rows.Add((reader.GetString(0), familyKey, familyName));
        }
        return rows;
    }

    private static string VariantIdParams(SqliteCommand cmd, IReadOnlyList<ActualizationVariant> variants)
    {
        var idParams = new string[variants.Count];
        for (var p = 0; p < variants.Count; p++)
        {
            idParams[p] = "@vid" + p;
            cmd.Parameters.Add(new SqliteParameter(idParams[p], variants[p].VersionId));
        }
        return string.Join(", ", idParams);
    }
}
