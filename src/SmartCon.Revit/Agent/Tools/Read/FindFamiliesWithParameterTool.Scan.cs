using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Services.Agent;
using SmartCon.Revit.Agent.Tools.Families;

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
        CancellationToken ct)
    {
        var outcome = new ScanOutcome();
        var resolver = summary ? new AgentSharedParameterResolver(doc) : null;

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

                if (resolver is not null)
                    AddShared(scan, resolver, parameter, name!);

                if (!MatchesName(name!, parameterName, mode))
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
                    ["value"] = value
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
                summary,
                resolver,
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
        bool summary,
        AgentSharedParameterResolver? resolver,
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

                if (resolver is not null)
                    AddShared(scan, resolver, parameter, name!);

                if (!MatchesName(name!, parameterName, mode))
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
                        FirstValue = value
                    };
                    scan.InstanceParameters.Add(name!, match);
                    scan.MatchedInstanceParametersTotal++;
                    scan.MatchedParameterNames.Add(name!);

                    if (scan.InstanceMatches.Count < maxMatchedTypes)
                        scan.InstanceMatches.Add(match.ToRow());
                }

                match.AddInstance(instance.Id.GetValue(), value);
                if (match.Row is not null)
                    match.SyncRow();
            }
        }
    }

    private static void AddShared(
        FamilyScan scan,
        AgentSharedParameterResolver resolver,
        Parameter parameter,
        string name)
    {
        var guid = resolver.ResolveGuid(parameter);
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

    private sealed class ScanOutcome
    {
        public Dictionary<long, FamilyScan> Families { get; } = new();

        public HashSet<long> FamiliesSeen { get; } = new();

        public int TypesScanned { get; set; }

        public int InstancesScanned { get; set; }
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
                ["sampleValues"] = SampleValues
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
