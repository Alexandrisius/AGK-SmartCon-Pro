using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

/// <summary>
/// Regenerates one version's <c>family_dependencies</c> rows in place
/// (ADR-072 World B): non-routing kinds (shared_nested) carry over verbatim;
/// routing links are deleted and rebuilt from the given routing rules with
/// the same resolution <c>DependencyLinkWriter</c> uses at import (normalized
/// family name → loadable catalog item; the Segments group never produces
/// links; dedup per child+kind). Shared by the routing editor (save) and
/// <c>SetActiveVersionAsync</c> (the activated version's links must reflect
/// the item-level rules — audit M12). Caller owns the transaction.
/// </summary>
internal static class RoutingDependencyLinkRebuilder
{
    public static async Task<int> RebuildAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        IFamilyCatalogProvider catalog,
        string catalogItemId,
        string versionId,
        IReadOnlyList<FamilyRoutingRuleInfo> rules,
        CancellationToken ct)
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
            cmd.Parameters.Add(new SqliteParameter("@sourceVid", versionId));
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
            delRouting.Parameters.Add(new SqliteParameter("@version", versionId));
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

        var partNames = rules
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
            var child = await catalog
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
            ins.Parameters.Add(new SqliteParameter("@version", versionId));
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
