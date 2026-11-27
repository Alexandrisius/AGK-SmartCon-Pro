namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Summary projections of the scan outcome: byCategory, the prefix
/// landscape, the per-name migration inventory and the scope-verification
/// report block.
/// </summary>
public sealed partial class FindFamiliesWithParameterTool
{
    private static List<Dictionary<string, object?>> BuildByCategory(
        IReadOnlyList<FamilyScan> matched) =>
        matched
            .GroupBy(item => item.Category ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group => new Dictionary<string, object?>
            {
                ["category"] = group.Key.Length == 0 ? null : group.Key,
                ["families"] = group.Count()
            })
            .OrderByDescending(row => (int)row["families"]!)
            .ThenBy(row => (string?)row["category"], StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static Dictionary<string, object?> BuildSummary(
        ScanOutcome outcome,
        bool instancesScanned)
    {
        var families = outcome.Families.Values.ToList();
        var withoutShared = families.Count(item => item.SharedGuids.Count == 0);
        var buckets = new Dictionary<string, PrefixBucket>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in families)
        {
            foreach (var name in family.SharedParameterNames)
            {
                var prefix = PrefixOf(name);
                if (!buckets.TryGetValue(prefix, out var bucket))
                {
                    bucket = new PrefixBucket(prefix);
                    buckets.Add(prefix, bucket);
                }

                bucket.Families.Add(family.FamilyId);
                bucket.Parameters.Add(name);
            }
        }

        var ordered = buckets.Values
            .OrderByDescending(bucket => bucket.Families.Count)
            .ThenBy(bucket => bucket.Prefix, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var byPrefix = ordered
            .Take(MaxPrefixBuckets)
            .Select(bucket => new Dictionary<string, object?>
            {
                ["prefix"] = bucket.Prefix,
                ["families"] = bucket.Families.Count,
                ["parameters"] = bucket.Parameters.Count
            })
            .ToList();

        return new Dictionary<string, object?>
        {
            ["familiesWithoutAnyShared"] = withoutShared,
            ["familiesWithSharedParameters"] = families.Count - withoutShared,
            ["placedFamilies"] = instancesScanned
                ? families.Count(item => item.PlacedInstanceCount > 0)
                : (int?)null,
            ["byPrefix"] = byPrefix,
            ["byPrefixTruncated"] = ordered.Count > byPrefix.Count,
            ["byParameterName"] = BuildByParameterName(outcome),
            ["byParameterNameTruncated"] = outcome.ByParameterName.Count > MaxParameterNameRows,
            ["summaryScope"] = instancesScanned ? "type+instance" : "type",
            ["summaryNote"] =
                "familiesWithoutAnyShared and byPrefix count shared parameters only. " +
                "byParameterName aggregates the MATCHED names only (the query's matches), " +
                "not every parameter of every family. " +
                (instancesScanned
                    ? "Scope is type+instance."
                    : "Scope is type only: pass includeInstanceParameters=true for instance-scope " +
                      "shared parameters and unplaced counts.")
        };
    }

    private static List<Dictionary<string, object?>> BuildByParameterName(ScanOutcome outcome) =>
        outcome.ByParameterName.Values
            .OrderByDescending(aggregate => aggregate.Families.Count)
            .ThenBy(aggregate => aggregate.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxParameterNameRows)
            .Select(aggregate => new Dictionary<string, object?>
            {
                ["name"] = aggregate.Name,
                ["guid"] = aggregate.SingleGuid,
                ["isShared"] = aggregate.SingleGuid is not null,
                ["families"] = aggregate.Families.Count,
                ["scopes"] = aggregate.Families
                    .Select(id => outcome.Families.TryGetValue(id, out var scan)
                        && scan.ScopeByName.TryGetValue(aggregate.Name, out var scope)
                            ? scope
                            : null)
                    .Where(scope => scope is not null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                ["verifiedFamilies"] = aggregate.Families.Count(id =>
                    outcome.Families.TryGetValue(id, out var scan)
                    && string.Equals(scan.ScopeVerification, "verified", StringComparison.Ordinal)
                    && scan.ScopeByName.ContainsKey(aggregate.Name))
            })
            .ToList();

    private static Dictionary<string, object?> BuildScopeVerification(
        ScanOutcome outcome,
        bool verifyFamilyScope)
    {
        if (!verifyFamilyScope)
        {
            return new Dictionary<string, object?>
            {
                ["mode"] = "assumed",
                ["note"] =
                    "verifyFamilyScope=false: every 'family+project' scope is an assumption from " +
                    "the project binding; pass verifyFamilyScope=true (the default) to prove it " +
                    "against the family documents."
            };
        }

        var stats = outcome.ScopeVerificationStats;
        return new Dictionary<string, object?>
        {
            ["mode"] = "family-document",
            ["candidates"] = stats?.Candidates,
            ["verified"] = stats?.Verified,
            ["failed"] = stats?.Failed,
            ["unverified"] = stats?.Unverified,
            ["limit"] = stats?.Limit,
            ["offset"] = stats?.Offset,
            ["nextVerificationOffset"] = stats?.Truncated == true ? stats?.NextOffset : null,
            ["truncated"] = stats?.Truncated,
            ["note"] =
                "Verification opens each candidate family document and matches the parameter by " +
                "GUID (fallback by name). 'unverified' rows keep the assumed scope: continue with " +
                "verificationOffset, or check one family with get_family_parameters."
        };
    }

    /// <summary>
    /// Validates parameterScope: a typo ("projects") used to slip through as a
    /// silent empty result instead of invalid_arguments (validator note #3).
    /// </summary>
}
