using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Revit.Agent.Query;
using SmartCon.Revit.Agent.Tools;

namespace SmartCon.Revit.Agent.Classification;

public sealed partial class ClassifyTypesTool
{
    private const int MaxValueLength = 120;

    /// <summary>Copies everything the matching needs into plain DTOs. Revit thread only.</summary>
    private ClassifySnapshot Collect(Document doc, JsonElement args, CancellationToken ct)
    {
        var parsed = ClassifyArgs.Parse(args);
        if (parsed.Error is not null)
            return ClassifySnapshot.Failed(ToolResult.Fail("invalid_arguments", parsed.Error));

        var table = AgentClassificationReader.Read(doc);
        if (!table.Loaded)
        {
            return ClassifySnapshot.Failed(ToolResult.Fail(
                AgentClassificationGuard.TableNotLoaded,
                TableNotLoadedMessage,
                data: AgentJson.Serialize(new { loaded = false })));
        }

        List<ElementType>? directTypes = null;
        if (parsed.TypeIds.Count > 0)
        {
            var missing = new List<long>();
            directTypes = new List<ElementType>();
            foreach (var id in parsed.TypeIds)
            {
                if (doc.GetElement(ElementIdCompat.Create(id)) is ElementType type
                    && type.get_Parameter(AgentParameterResolver.AssemblyCodeBuiltIn) is not null)
                {
                    directTypes.Add(type);
                }
                else
                {
                    missing.Add(id);
                }
            }

            if (missing.Count > 0)
            {
                return ClassifySnapshot.Failed(ToolResult.Fail(
                    "type_not_found",
                    "typeIds requires every id to be an element type with the Assembly Code " +
                    "parameter; missing: [" + string.Join(", ", missing) + "].",
                    data: AgentJson.Serialize(new { missing })));
            }
        }

        List<Autodesk.Revit.DB.Family>? families = null;
        if (parsed.FamilyIds.Count > 0)
        {
            families = new List<Autodesk.Revit.DB.Family>();
            var missing = new List<long>();
            foreach (var id in parsed.FamilyIds)
            {
                if (doc.GetElement(ElementIdCompat.Create(id)) is Autodesk.Revit.DB.Family family)
                    families.Add(family);
                else
                    missing.Add(id);
            }

            if (families.Count == 0 || missing.Count > 0)
            {
                return ClassifySnapshot.Failed(ToolResult.Fail(
                    "family_not_found",
                    "familyIds requires every id to resolve to a family; missing: [" +
                    string.Join(", ", missing) + "]."));
            }
        }

        HashSet<long>? categoryIds = null;
        if (parsed.Categories.Count > 0)
        {
            var unknown = new List<string>();
            categoryIds = new HashSet<long>(
                AgentCategoryResolver.Resolve(doc, parsed.Categories, unknown)
                    .Select(id => id.GetValue()));
            if (unknown.Count > 0)
            {
                var first = unknown[0];
                return ClassifySnapshot.Failed(ToolResult.Fail(
                    "unknown_category",
                    "Unknown categor" + (unknown.Count == 1 ? "y: " : "ies: ") +
                    string.Join(", ", unknown) + "." +
                    AgentCategoryResolver.SuggestSuffix(doc, first)));
            }
        }

        var familyIdSet = families is null
            ? null
            : new HashSet<long>(families.Select(family => family.Id.GetValue()));

        var candidates = new List<(ElementType Type, string? Current)>();
        IEnumerable<ElementType> scan = directTypes ?? new FilteredElementCollector(doc)
            .OfClass(typeof(ElementType))
            .Cast<ElementType>();
        foreach (var type in scan)
        {
            ct.ThrowIfCancellationRequested();
            var codeParameter = type.get_Parameter(AgentParameterResolver.AssemblyCodeBuiltIn);
            if (codeParameter is null)
                continue;

            if (categoryIds is not null
                && (type.Category is null || !categoryIds.Contains(type.Category.Id.GetValue())))
            {
                continue;
            }

            if (familyIdSet is not null
                && (type is not FamilySymbol symbol
                    || symbol.Family is null
                    || !familyIdSet.Contains(symbol.Family.Id.GetValue())))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(parsed.NameContains)
                && NameIndexOf(type, parsed.NameContains!) < 0)
            {
                continue;
            }

            candidates.Add((type, ReadCode(codeParameter)));
        }

        candidates.Sort((left, right) =>
        {
            var byFamily = string.Compare(
                FamilyNameOf(left.Type), FamilyNameOf(right.Type), StringComparison.OrdinalIgnoreCase);
            return byFamily != 0
                ? byFamily
                : string.Compare(left.Type.Name, right.Type.Name, StringComparison.OrdinalIgnoreCase);
        });

        var typesTotal = candidates.Count;
        var selected = new List<(ElementType Type, string? Current)>();
        var skippedAlreadySet = 0;
        foreach (var candidate in candidates)
        {
            if (parsed.OnlyEmpty && !string.IsNullOrEmpty(candidate.Current))
            {
                skippedAlreadySet++;
                continue;
            }

            selected.Add(candidate);
        }

        var truncated = false;
        if (selected.Count > parsed.MaxTypes)
        {
            selected = selected.Take(parsed.MaxTypes).ToList();
            truncated = true;
        }

        var types = new List<SmartCon.Core.Services.Agent.ClassifyTypeSnapshot>(selected.Count);
        foreach (var (type, current) in selected)
        {
            types.Add(new SmartCon.Core.Services.Agent.ClassifyTypeSnapshot(
                type.Id.GetValue(),
                type.Category?.Name ?? string.Empty,
                FamilyNameOf(type),
                type is FamilySymbol symbol && symbol.Family is not null
                    ? symbol.Family.Id.GetValue()
                    : null,
                type.Name,
                string.IsNullOrEmpty(current) ? null : current,
                TextValuesOf(doc, type, parsed.TextParameters, ct)));
        }

        return new ClassifySnapshot
        {
            Args = parsed,
            Table = table,
            Types = types,
            TypesTotal = typesTotal,
            SkippedAlreadySet = skippedAlreadySet,
            Truncated = truncated
        };
    }

    private static int NameIndexOf(ElementType type, string nameContains) =>
        System.Math.Max(
            FamilyNameOf(type).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase),
            type.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase));

    private static string FamilyNameOf(ElementType type)
    {
        try
        {
            return type.FamilyName ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string? ReadCode(Parameter parameter)
    {
        try
        {
            if (!parameter.HasValue)
                return null;

            var code = parameter.AsString();
            return string.IsNullOrWhiteSpace(code) ? null : code!.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<string> TextValuesOf(
        Document doc,
        ElementType type,
        IReadOnlyList<string> extraNames,
        CancellationToken ct)
    {
        var values = new List<string>();
        AddValue(values, ValueOf(type.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)));
        AddValue(values, ValueOf(type.get_Parameter(BuiltInParameter.ALL_MODEL_MANUFACTURER)));
        AddValue(values, ValueOf(type.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL)));
        AddValue(values, ValueOf(type.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)));
        foreach (var name in extraNames)
        {
            ct.ThrowIfCancellationRequested();
            AddValue(values, ValueOf(AgentParameterResolver.Find(doc, type, name, true)));
        }

        return values;
    }

    private static void AddValue(List<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !values.Contains(value!, StringComparer.OrdinalIgnoreCase))
            values.Add(value!);
    }

    private static string? ValueOf(Parameter? parameter)
    {
        if (parameter is null || !parameter.HasValue)
            return null;

        try
        {
            var text = parameter.AsValueString();
            if (string.IsNullOrWhiteSpace(text))
                text = parameter.AsString();
            if (string.IsNullOrWhiteSpace(text))
                return null;

            text = text!.Trim();
            return text.Length <= MaxValueLength ? text : text.Substring(0, MaxValueLength - 3) + "...";
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>The parsed and validated arguments of one classify_types call.</summary>
internal sealed class ClassifyArgs
{
    private const int DefaultMaxTypes = 400;
    private const int HardMaxTypes = 2000;
    private const int DefaultMaxFamilies = 150;
    private const int HardMaxFamilies = 600;

    public static readonly ClassifyArgs Default = new();

    public IReadOnlyList<string> Categories { get; private init; } = Array.Empty<string>();

    public IReadOnlyList<long> FamilyIds { get; private init; } = Array.Empty<long>();

    public IReadOnlyList<long> TypeIds { get; private init; } = Array.Empty<long>();

    public string? NameContains { get; private init; }

    public bool OnlyEmpty { get; private init; } = true;

    public int MaxTypes { get; private init; } = DefaultMaxTypes;

    public IReadOnlyList<string> TextParameters { get; private init; } = Array.Empty<string>();

    public int MaxFamilies { get; private init; } = DefaultMaxFamilies;

    public double MinScore { get; private init; } = ClassifyOptions.DefaultMinScore;

    public int CandidateDepth { get; private init; } = ClassifyOptions.DefaultCandidateDepth;

    public string? Instructions { get; private init; }

    public string? Error { get; private init; }

    public static ClassifyArgs Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return new ClassifyArgs { Error = "Arguments must be an object." };

        var maxTypes = AgentJson.GetInt(args, "maxTypes", DefaultMaxTypes);
        if (maxTypes < 1 || maxTypes > HardMaxTypes)
        {
            return new ClassifyArgs
            {
                Error = "maxTypes must be between 1 and " + HardMaxTypes + ", got " + maxTypes + "."
            };
        }

        var maxFamilies = AgentJson.GetInt(args, "maxFamilies", DefaultMaxFamilies);
        if (maxFamilies < 1 || maxFamilies > HardMaxFamilies)
        {
            return new ClassifyArgs
            {
                Error = "maxFamilies must be between 1 and " + HardMaxFamilies + ", got " +
                        maxFamilies + "."
            };
        }

        var minScore = ClassifyOptions.DefaultMinScore;
        if (AgentJson.TryGetDouble(args, "minScore", out var parsedScore))
        {
            if (parsedScore < 0 || parsedScore > 1)
            {
                return new ClassifyArgs
                {
                    Error = "minScore must be a number between 0 and 1, got " + parsedScore + "."
                };
            }

            minScore = parsedScore;
        }

        var candidateDepth = AgentJson.GetInt(args, "candidateDepth", ClassifyOptions.DefaultCandidateDepth);
        if (candidateDepth < ClassifyOptions.MinCandidateDepth
            || candidateDepth > ClassifyOptions.MaxCandidateDepth)
        {
            return new ClassifyArgs
            {
                Error = "candidateDepth must be between " + ClassifyOptions.MinCandidateDepth +
                        " and " + ClassifyOptions.MaxCandidateDepth + ", got " + candidateDepth + "."
            };
        }

        var instructions = AgentJson.GetString(args, "instructions");

        return new ClassifyArgs
        {
            Categories = AgentJson.GetStringArray(args, "categories"),
            FamilyIds = AgentJson.GetLongArray(args, "familyIds"),
            TypeIds = AgentJson.GetLongArray(args, "typeIds"),
            NameContains = AgentJson.GetString(args, "nameContains"),
            OnlyEmpty = AgentJson.GetBool(args, "onlyEmpty", true),
            MaxTypes = maxTypes,
            TextParameters = AgentJson.GetStringArray(args, "textParameters"),
            MaxFamilies = maxFamilies,
            MinScore = minScore,
            CandidateDepth = candidateDepth,
            Instructions = instructions
        };
    }
}

/// <summary>
/// Everything <see cref="ClassifyTypesTool"/> copied off the Revit thread, or
/// the typed failure that stopped the collection.
/// </summary>
internal sealed class ClassifySnapshot
{
    public ToolResult? Failure { get; internal set; }

    public ClassifyArgs Args { get; internal set; } = ClassifyArgs.Default;

    public AgentClassificationTable Table { get; internal set; } = AgentClassificationTable.Empty;

    public List<SmartCon.Core.Services.Agent.ClassifyTypeSnapshot> Types { get; internal set; } = new();

    /// <summary>Types matching the selection filters, before the onlyEmpty cut.</summary>
    public int TypesTotal { get; internal set; }

    public int SkippedAlreadySet { get; internal set; }

    public bool Truncated { get; internal set; }

    public string? DocumentVersion { get; set; }

    public static ClassifySnapshot Failed(ToolResult result) => new() { Failure = result };
}
