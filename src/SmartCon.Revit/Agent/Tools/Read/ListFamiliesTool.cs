using System.Text.Json;
using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using RevitFamilyManager = Autodesk.Revit.DB.FamilyManager;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Lists the loadable families loaded in the project. Family, family type and
/// family instance are different things: this tool returns families only.
/// </summary>
public sealed class ListFamiliesTool : AgentReadToolBase
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    public ListFamiliesTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_families",
        Description =
            "Lists the loadable families loaded in the project: familyId, name, category, " +
            "number of types, isEditable and isInPlace. Families are not types and not " +
            "instances: use list_family_types for the types of a family.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""nameContains"":{""type"":""string""},""category"":{""type"":""string""},""includeInPlace"":{""type"":""boolean"",""default"":true},""includeSystemFamilies"":{""type"":""boolean"",""default"":false},""limit"":{""type"":""integer"",""default"":100,""maximum"":500},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var nameContains = AgentJson.GetString(args, "nameContains");
        var category = AgentJson.GetString(args, "category");
        var includeInPlace = AgentJson.GetBool(args, "includeInPlace", true);
        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));
        var limit = AgentPaging.NormalizeLimit(
            AgentJson.GetInt(args, "limit", DefaultLimit),
            DefaultLimit,
            MaxLimit);

        var matches = new List<Dictionary<string, object?>>();
        var total = 0;
        foreach (var family in AgentFamilyReader.LoadableFamilies(doc))
        {
            ct.ThrowIfCancellationRequested();

            if (!includeInPlace && family.IsInPlace)
                continue;

            if (!string.IsNullOrWhiteSpace(nameContains)
                && !AgentFamilySpecs.ContainsNormalized(family.Name, nameContains!))
            {
                continue;
            }

            var categoryName = family.FamilyCategory?.Name;
            if (!string.IsNullOrWhiteSpace(category)
                && !MatchesCategory(family, category!))
            {
                continue;
            }

            total++;
            if (total <= offset || matches.Count >= limit)
                continue;

            matches.Add(new Dictionary<string, object?>
            {
                ["familyId"] = family.Id.GetValue(),
                ["name"] = family.Name,
                ["category"] = categoryName,
                ["categoryId"] = family.FamilyCategory?.Id.GetValue(),
                ["typeCount"] = family.GetFamilySymbolIds().Count,
                ["isEditable"] = family.IsEditable,
                ["isInPlace"] = family.IsInPlace,
                ["isParametric"] = family.IsParametric
            });
        }

        var nextOffset = offset + matches.Count;
        var payload = new Dictionary<string, object?>
        {
            ["families"] = matches,
            ["total"] = total,
            ["returned"] = matches.Count,
            ["offset"] = offset,
            ["nextOffset"] = nextOffset < total ? nextOffset : (int?)null,
            ["truncated"] = nextOffset < total
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Loaded families: " + matches.Count + " of " + total +
            (nextOffset < total ? " (next offset " + nextOffset + ")" : string.Empty) + ".");
    }

    private static bool MatchesCategory(RevitFamily family, string category)
    {
        var name = family.FamilyCategory?.Name;
        if (!string.IsNullOrEmpty(name)
            && string.Equals(name, category.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var builtIn = "OST_" + category.Trim().Replace("OST_", string.Empty);
        var categoryId = family.FamilyCategory?.Id;
        if (categoryId is null)
            return false;

        return string.Equals(
            ((BuiltInCategory)categoryId.GetValue()).ToString(),
            builtIn,
            StringComparison.OrdinalIgnoreCase);
    }
}