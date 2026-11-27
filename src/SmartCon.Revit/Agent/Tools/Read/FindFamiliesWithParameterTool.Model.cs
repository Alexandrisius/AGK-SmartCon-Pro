using Autodesk.Revit.DB;
using SmartCon.Revit.Agent.Tools.Project;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>Scan state and per-family/per-name models of the parameter scan.</summary>
public sealed partial class FindFamiliesWithParameterTool
{
    private sealed class ProjectBindingIndex
    {
        private readonly Dictionary<string, HashSet<string>> _categoriesByGuid =
            new(StringComparer.Ordinal);

        private readonly Dictionary<string, HashSet<string>> _categoriesByName =
            new(StringComparer.OrdinalIgnoreCase);

        public ProjectBindingIndex(Document doc)
        {
            try
            {
                foreach (var row in AgentProjectParameterReader.Collect(doc))
                {
                    if (row.Guid is not null)
                        AddKey(_categoriesByGuid, row.Guid.Value.ToString("D"), row);
                    AddKey(_categoriesByName, row.Name, row);
                }
            }
            catch (Exception)
            {
                // A binding pass failure must not break the scan: scope falls back
                // to "family" everywhere.
            }
        }

        private static void AddKey(
            Dictionary<string, HashSet<string>> map,
            string key,
            AgentProjectParameterReader.ProjectParameterRow row)
        {
            if (!map.TryGetValue(key, out var categories))
            {
                categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                map.Add(key, categories);
            }

            foreach (var category in row.Categories)
                categories.Add(category);
        }

        /// <summary>
        /// "family" (only the family document can explain the match — nothing
        /// binds it at project level), "project" (a name-only binding: the
        /// parameter is not shared) or "family+project" (the shared GUID is
        /// bound at project level AND the match may come from the family
        /// document — WHICH of the two produced it is only provable by opening
        /// the family document, see VerifyFamilyScopes).
        /// </summary>
        public string ScopeOf(string name, string? guid, Category? familyCategory)
        {
            HashSet<string>? guidCategories = null;
            HashSet<string>? nameCategories = null;
            var boundByGuid = guid is not null
                              && _categoriesByGuid.TryGetValue(guid!, out guidCategories);
            var boundByName = _categoriesByName.TryGetValue(name, out nameCategories);
            if (!boundByGuid && !boundByName)
                return "family";

            var categories = boundByGuid ? guidCategories! : nameCategories!;
            var categoryMatches = familyCategory is null
                                  || categories.Count == 0
                                  || categories.Contains(familyCategory.Name);
            if (!categoryMatches)
                return "family";

            return boundByGuid && guid is not null ? "family+project" : "project";
        }
    }

    private sealed class ScanOutcome
    {
        public Dictionary<long, FamilyScan> Families { get; } = new();

        public HashSet<long> FamiliesSeen { get; } = new();

        public Dictionary<string, ParameterNameAggregate> ByParameterName { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public ScopeVerificationStats? ScopeVerificationStats { get; set; }

        public int TypesScanned { get; set; }

        public int InstancesScanned { get; set; }

        public void AggregateName(long familyId, string name, string? guid)
        {
            if (!ByParameterName.TryGetValue(name, out var aggregate))
            {
                aggregate = new ParameterNameAggregate(name);
                ByParameterName.Add(name, aggregate);
            }

            aggregate.Families.Add(familyId);
            if (guid is not null)
                aggregate.Guid ??= guid;
            else
                aggregate.SeenNonShared = true;
        }
    }

    private sealed class ScopeVerificationStats
    {
        public int Candidates { get; init; }

        public int Verified { get; init; }

        public int Failed { get; init; }

        public int SkippedByOffset { get; init; }

        public int Unverified { get; init; }

        public bool Truncated { get; init; }

        public int Limit { get; init; }

        public int Offset { get; init; }

        /// <summary>
        /// Where the next verification pass must continue: every candidate this
        /// pass actually touched. SkippedByOffset already counts the offset
        /// prefix (it is min(offset, candidates.Count)), so adding Offset again
        /// would skip a whole page (validator v16, P1-1).
        /// </summary>
        public int NextOffset => SkippedByOffset + Verified + Failed;
    }

    private sealed class ParameterNameAggregate
    {
        public ParameterNameAggregate(string name) => Name = name;

        public string Name { get; }

        public HashSet<long> Families { get; } = new();

        public string? Guid { get; set; }

        public bool SeenNonShared { get; set; }

        public string? SingleGuid => SeenNonShared ? null : Guid;
    }

    private sealed class FamilyScan
    {
        public long FamilyId { get; init; }

        public required string FamilyUniqueId { get; init; }

        public required string FamilyName { get; init; }

        public string? Category { get; init; }

        public int PlacedInstanceCount { get; set; }

        public HashSet<string> SharedGuids { get; } = new(StringComparer.Ordinal);

        public HashSet<string> SharedParameterNames { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Matched parameter name → its CURRENT scope (assumed or verified).</summary>
        public Dictionary<string, string> ScopeByName { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Matched parameter name → shared GUID when one was seen.</summary>
        public Dictionary<string, string?> GuidByName { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Matched parameter name → number of types that expose it.</summary>
        public Dictionary<string, int> TypeMatchesByName { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Names matched on placed instances (distinct names = matches).</summary>
        public HashSet<string> InstanceNames
            => new(InstanceParameters.Keys, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// null = nothing to verify (no project-bound assumption) or
        /// verification disabled; "verified" / "unverified" (beyond the limit
        /// or offset) / "failed" (the family document could not be opened).
        /// </summary>
        public string? ScopeVerification { get; set; }

        public string? ScopeVerifyError { get; set; }

        public List<Dictionary<string, object?>> TypeMatches { get; } = new();

        public List<Dictionary<string, object?>> InstanceMatches { get; } = new();

        public Dictionary<string, InstanceParameterMatch> InstanceParameters { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public bool HasMatches => ScopeByName.Count > 0;

        public int MatchedTypesTotal => TypeMatchesByName.Values.Sum();

        public int MatchedInstanceParametersTotal => InstanceParameters.Count;

        public HashSet<string> MatchedParameterNames =>
            new(ScopeByName.Keys, StringComparer.OrdinalIgnoreCase);

        public List<string> ParameterScopes =>
            ScopeByName.Values.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>
        /// Keeps the strongest scope seen for the name: a project-bound
        /// occurrence outranks a plain family one, because the binding is what
        /// makes the scope ambiguous. <paramref name="typeMatch"/> counts the
        /// occurrence towards the type-match totals (the instance pass merges
        /// the scope without inflating them).
        /// </summary>
        public void MergeScope(string name, string scope, string? guid, bool typeMatch)
        {
            if (ScopeByName.TryGetValue(name, out var current)
                && Rank(current) >= Rank(scope))
            {
                // keep the stronger existing scope
            }
            else
            {
                ScopeByName[name] = scope;
            }

            if (typeMatch)
            {
                // GetValueOrDefault is not available on Dictionary over net48.
                TypeMatchesByName.TryGetValue(name, out var seen);
                TypeMatchesByName[name] = seen + 1;
            }
            if (guid is not null || !GuidByName.ContainsKey(name))
                GuidByName[name] = guid;
        }

        private static int Rank(string scope) => scope switch
        {
            "family+project" => 2,
            "project" => 1,
            _ => 0
        };

        public void ApplyVerifiedScope(
            string name,
            string scope,
            bool presentInFamilyDocument,
            bool? familyDocumentIsInstance)
        {
            ScopeByName[name] = scope;
            foreach (var row in TypeMatches)
            {
                if (!string.Equals((string?)row["parameterName"], name, StringComparison.OrdinalIgnoreCase))
                    continue;

                row["parameterScope"] = scope;
                row["familyDocument"] = presentInFamilyDocument;
                row["familyDocumentIsInstance"] = familyDocumentIsInstance;
            }

            if (InstanceParameters.TryGetValue(name, out var match))
            {
                match.ParameterScope = scope;
                match.FamilyDocument = presentInFamilyDocument;
                match.FamilyDocumentIsInstance = familyDocumentIsInstance;
                match.SyncRow();
            }
        }
    }

    private sealed class InstanceParameterMatch
    {
        public required string ParameterName { get; init; }

        public string? FirstValue { get; init; }

        public string? Guid { get; init; }

        public string ParameterScope { get; set; } = "family";

        public bool? FamilyDocument { get; set; }

        public bool? FamilyDocumentIsInstance { get; set; }

        public int InstanceCount { get; private set; }

        public List<long> SampleInstanceIds { get; } = new();

        public List<string> SampleValues { get; } = new();

        public Dictionary<string, object?>? Row { get; private set; }

        public void AddInstance(long instanceId, string? value)
        {
            InstanceCount++;
            if (SampleInstanceIds.Count < MaxSampleInstances
                && !SampleInstanceIds.Contains(instanceId))
            {
                SampleInstanceIds.Add(instanceId);
            }

            if (SampleValues.Count < MaxSampleInstances
                && !string.IsNullOrWhiteSpace(value)
                && !SampleValues.Contains(value!))
            {
                SampleValues.Add(value!);
            }
        }

        public Dictionary<string, object?> ToRow()
        {
            Row = new Dictionary<string, object?>
            {
                ["scope"] = "instance",
                ["parameterName"] = ParameterName,
                ["value"] = FirstValue,
                ["instanceCount"] = 0,
                ["sampleInstanceIds"] = SampleInstanceIds,
                ["sampleValues"] = SampleValues,
                ["parameterScope"] = ParameterScope,
                ["isShared"] = Guid is not null,
                ["guid"] = Guid,
                ["familyDocument"] = FamilyDocument,
                ["familyDocumentIsInstance"] = FamilyDocumentIsInstance
            };
            return Row;
        }

        public void SyncRow()
        {
            if (Row is null)
                return;

            Row["instanceCount"] = InstanceCount;
            Row["value"] = FirstValue ?? (SampleValues.Count > 0 ? SampleValues[0] : null);
            Row["sampleInstanceIds"] = SampleInstanceIds;
            Row["sampleValues"] = SampleValues;
            Row["parameterScope"] = ParameterScope;
            Row["familyDocument"] = FamilyDocument;
            Row["familyDocumentIsInstance"] = FamilyDocumentIsInstance;
        }
    }
}
