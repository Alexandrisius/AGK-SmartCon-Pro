using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Services.Agent;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Project;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed partial class FindFamiliesWithParameterTool
{
    private static ScanOutcome ScanFamilies(
        Document doc,
        string parameterName,
        MatchMode mode,
        string? valueContains,
        HashSet<long>? categoryIds,
        bool includeInstanceParameters,
        bool summary,
        int maxMatchedTypes,
        bool requireShared,
        string? parameterScopeFilter,
        CancellationToken ct)
    {
        var outcome = new ScanOutcome();
        // The resolver is cheap after the first hit (definitionId cache) and the
        // binding index is one BindingMap pass: both are needed for isShared and
        // the family|project scope on EVERY match, not only in summary mode.
        var resolver = new AgentSharedParameterResolver(doc);
        var bindingIndex = new ProjectBindingIndex(doc);

        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)))
        {
            ct.ThrowIfCancellationRequested();
            if (element is not FamilySymbol symbol)
                continue;

            outcome.TypesScanned++;
            var family = symbol.Family;
            if (family is null)
                continue;

            outcome.FamiliesSeen.Add(family.Id.GetValue());
            if (!MatchesCategory(family, categoryIds))
                continue;

            var scan = GetOrCreate(outcome.Families, family);
            var seenParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Parameter parameter in symbol.Parameters)
            {
                var name = parameter.Definition?.Name;
                if (string.IsNullOrEmpty(name))
                    continue;

                var guid = resolver.ResolveGuid(parameter);
                AddShared(scan, guid, name!);

                if (!MatchesName(name!, parameterName, mode))
                    continue;

                if (requireShared && guid is null)
                    continue;

                var parameterScope = bindingIndex.ScopeOf(name!, guid, family.FamilyCategory);
                if (!MatchesParameterScope(parameterScope, parameterScopeFilter))
                    continue;

                var value = SafeValueString(parameter);
                if (!string.IsNullOrWhiteSpace(valueContains)
                    && !AgentFamilySpecs.ContainsNormalized(value, valueContains!))
                {
                    continue;
                }

                if (!seenParameterNames.Add(name!))
                    continue;

                scan.MatchedTypesTotal++;
                scan.MatchedParameterNames.Add(name!);
                scan.ParameterScopes.Add(parameterScope);
                outcome.AggregateName(scan.FamilyId, name!, guid, parameterScope);

                // The cap belongs to the family, not to one type: a door family with
                // 40 matching types used to return 40 rows (external run 6, P2).
                if (scan.TypeMatches.Count >= maxMatchedTypes)
                    continue;

                scan.TypeMatches.Add(new Dictionary<string, object?>
                {
                    ["scope"] = "type",
                    ["typeId"] = symbol.Id.GetValue(),
                    ["typeName"] = symbol.Name,
                    ["parameterName"] = name,
                    ["value"] = value,
                    ["parameterScope"] = parameterScope,
                    ["isShared"] = guid is not null,
                    ["guid"] = guid
                });
            }
        }

        if (includeInstanceParameters)
        {
            ScanPlacedInstances(
                doc,
                categoryIds,
                parameterName,
                mode,
                valueContains,
                maxMatchedTypes,
                resolver,
                bindingIndex,
                requireShared,
                parameterScopeFilter,
                outcome,
                ct);
        }

        return outcome;
    }

    private static void ScanPlacedInstances(
        Document doc,
        HashSet<long>? categoryIds,
        string parameterName,
        MatchMode mode,
        string? valueContains,
        int maxMatchedTypes,
        AgentSharedParameterResolver resolver,
        ProjectBindingIndex bindingIndex,
        bool requireShared,
        string? parameterScopeFilter,
        ScanOutcome outcome,
        CancellationToken ct)
    {
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)))
        {
            ct.ThrowIfCancellationRequested();
            if (element is not FamilyInstance instance)
                continue;

            outcome.InstancesScanned++;
            var family = instance.Symbol?.Family;
            if (family is null)
                continue;

            outcome.FamiliesSeen.Add(family.Id.GetValue());
            if (!MatchesCategory(family, categoryIds))
                continue;

            var scan = GetOrCreate(outcome.Families, family);
            scan.PlacedInstanceCount++;

            var seenParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Parameter parameter in instance.Parameters)
            {
                var name = parameter.Definition?.Name;
                if (string.IsNullOrEmpty(name))
                    continue;

                var guid = resolver.ResolveGuid(parameter);
                AddShared(scan, guid, name!);

                if (!MatchesName(name!, parameterName, mode))
                    continue;

                if (requireShared && guid is null)
                    continue;

                var parameterScope = bindingIndex.ScopeOf(name!, guid, family.FamilyCategory);
                if (!MatchesParameterScope(parameterScope, parameterScopeFilter))
                    continue;

                var value = SafeValueString(parameter);
                if (!string.IsNullOrWhiteSpace(valueContains)
                    && !AgentFamilySpecs.ContainsNormalized(value, valueContains!))
                {
                    continue;
                }

                if (!seenParameterNames.Add(name!))
                    continue;

                if (!scan.InstanceParameters.TryGetValue(name!, out var match))
                {
                    match = new InstanceParameterMatch
                    {
                        ParameterName = name!,
                        FirstValue = value,
                        Guid = guid,
                        ParameterScope = parameterScope
                    };
                    scan.InstanceParameters.Add(name!, match);
                    scan.MatchedInstanceParametersTotal++;
                    scan.MatchedParameterNames.Add(name!);
                    scan.ParameterScopes.Add(parameterScope);
                    outcome.AggregateName(scan.FamilyId, name!, guid, parameterScope);

                    if (scan.InstanceMatches.Count < maxMatchedTypes)
                        scan.InstanceMatches.Add(match.ToRow());
                }

                match.AddInstance(instance.Id.GetValue(), value);
                if (match.Row is not null)
                    match.SyncRow();
            }
        }
    }

    private static bool MatchesParameterScope(string parameterScope, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)
            || string.Equals(filter, "any", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // "family+" is the migration-planning filter: family OR family+project —
        // everything that actually lives in family documents (the live run showed
        // half of the vendor-prefixed names are family+project; a strict "family"
        // filter silently dropped them).
        if (string.Equals(filter, "family+", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(parameterScope, "family", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(parameterScope, "family+project", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(parameterScope, filter, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddShared(
        FamilyScan scan,
        string? guid,
        string name)
    {
        if (guid is null)
            return;

        scan.SharedGuids.Add(guid);
        scan.SharedParameterNames.Add(name);
    }

    private static FamilyScan GetOrCreate(
        Dictionary<long, FamilyScan> families,
        Autodesk.Revit.DB.Family family)
    {
        var id = family.Id.GetValue();
        if (families.TryGetValue(id, out var existing))
            return existing;

        var created = new FamilyScan
        {
            FamilyId = id,
            FamilyUniqueId = family.UniqueId,
            FamilyName = family.Name,
            Category = family.FamilyCategory?.Name
        };
        families.Add(id, created);
        return created;
    }

    private static bool MatchesCategory(
        Autodesk.Revit.DB.Family family,
        HashSet<long>? categoryIds) =>
        categoryIds is null
        || (family.FamilyCategory is not null
            && categoryIds.Contains(family.FamilyCategory.Id.GetValue()));

    private static bool MatchesName(string name, string parameterName, MatchMode mode) =>
        mode switch
        {
            MatchMode.Contains => AgentFamilySpecs.ContainsNormalized(name, parameterName),
            MatchMode.Prefix => AgentFamilyText.NormalizeForMatch(name).StartsWith(
                AgentFamilyText.NormalizeForMatch(parameterName),
                StringComparison.Ordinal),
            _ => string.Equals(name, parameterName.Trim(), StringComparison.OrdinalIgnoreCase)
        };

    private static string? SafeValueString(Parameter parameter)
    {
        try
        {
            return parameter.AsValueString() ?? parameter.AsString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// One pass over doc.ParameterBindings: shared GUIDs (and non-shared names)
    /// bound at the PROJECT level. A parameter that is only bound here does not
    /// exist in the family document (the KRTRS_ lesson of the ФОП session:
    /// 194 "families" were project bindings, family docs held zero).
    /// </summary>
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
        /// "family" (lives in the family document), "project" (bound at project
        /// level; absent from the family document) or "family+project" (the same
        /// definition is embedded AND bound — public vendor GUIDs are exactly
        /// this case).
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

            // The parameter could come from either mechanism on this category;
            // only opening the family document (get_family_parameters) tells them
            // apart for a shared GUID that is also embedded.
            return boundByGuid && guid is not null ? "family+project" : "project";
        }
    }

    private sealed class ScanOutcome
    {
        public Dictionary<long, FamilyScan> Families { get; } = new();

        public HashSet<long> FamiliesSeen { get; } = new();

        public Dictionary<string, ParameterNameAggregate> ByParameterName { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public int TypesScanned { get; set; }

        public int InstancesScanned { get; set; }

        public void AggregateName(
            long familyId,
            string name,
            string? guid,
            string parameterScope)
        {
            if (!ByParameterName.TryGetValue(name, out var aggregate))
            {
                aggregate = new ParameterNameAggregate(name);
                ByParameterName.Add(name, aggregate);
            }

            aggregate.Families.Add(familyId);
            aggregate.Scopes.Add(parameterScope);
            if (guid is not null)
                aggregate.Guid ??= guid;
            else
                aggregate.SeenNonShared = true;
        }
    }

    private sealed class ParameterNameAggregate
    {
        public ParameterNameAggregate(string name) => Name = name;

        public string Name { get; }

        public HashSet<long> Families { get; } = new();

        public HashSet<string> Scopes { get; } = new(StringComparer.OrdinalIgnoreCase);

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

        public HashSet<string> MatchedParameterNames { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> ParameterScopes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int MatchedTypesTotal { get; set; }

        public int MatchedInstanceParametersTotal { get; set; }

        public List<Dictionary<string, object?>> TypeMatches { get; } = new();

        public List<Dictionary<string, object?>> InstanceMatches { get; } = new();

        public Dictionary<string, InstanceParameterMatch> InstanceParameters { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public bool Matched => MatchedTypesTotal > 0 || MatchedInstanceParametersTotal > 0;
    }

    private sealed class InstanceParameterMatch
    {
        public required string ParameterName { get; init; }

        public string? FirstValue { get; init; }

        public string? Guid { get; init; }

        public string ParameterScope { get; init; } = "family";

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
                ["guid"] = Guid
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
        }
    }
}
