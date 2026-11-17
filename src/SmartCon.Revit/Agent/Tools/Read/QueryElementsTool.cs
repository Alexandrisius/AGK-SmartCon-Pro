using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class QueryElementsTool : AgentReadToolBase
{
    public QueryElementsTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "query_elements",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Description =
            "Searches model elements with a structural QuerySpec and returns compact cards. " +
            "Narrow by categories (OST_... names or display names), levelIds, typeIds, viewId, roomId " +
            "and parameter conditions. Conditions support eq/neq/gt/gte/lt/lte/contains/startsWith/" +
            "endsWith/in/notIn/exists/notExists and are combined with AND or OR. " +
            "String comparisons are case-insensitive. For numeric conditions pass unit (mm recommended); " +
            "without unit the project display unit for that parameter is used. " +
            "Always set a reasonable limit (default 200, max 1000).",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""categories"":{""type"":""array"",""items"":{""type"":""string""}},""levelIds"":{""type"":""array"",""items"":{""type"":""integer""}},""typeIds"":{""type"":""array"",""items"":{""type"":""integer""}},""viewId"":{""type"":""integer""},""roomId"":{""type"":""integer""},""conditions"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""parameter"":{""type"":""string""},""op"":{""type"":""string"",""enum"":[""eq"",""neq"",""gt"",""gte"",""lt"",""lte"",""contains"",""startsWith"",""endsWith"",""in"",""notIn"",""exists"",""notExists""]},""value"":{},""unit"":{""type"":""string""}},""required"":[""parameter"",""op""],""additionalProperties"":false}},""logic"":{""type"":""string"",""enum"":[""and"",""or""],""default"":""and""},""includeTypeParameters"":{""type"":""boolean"",""default"":false},""limit"":{""type"":""integer"",""default"":200},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        QuerySpec? spec;
        try
        {
            spec = AgentJson.Deserialize<QuerySpec>(args);
        }
        catch (JsonException ex)
        {
            return ToolResult.Fail(
                "invalid_query",
                "The query is not valid JSON for QuerySpec: " + ex.Message);
        }

        if (spec is null)
            return ToolResult.Fail("invalid_query", "The query is empty.");

        var errors = spec.Validate();
        if (errors.Count > 0)
            return ToolResult.Fail("invalid_query", string.Join("; ", errors));

        if (spec.ViewId is { } viewId && doc.GetElement(ElementIdCompat.Create(viewId)) is not View)
        {
            return ToolResult.Fail(
                "invalid_view",
                "viewId " + viewId + " does not identify a view in this document.");
        }

        var query = new AgentElementQuery(doc);
        var result = query.Execute(spec, spec.Offset + spec.Limit, ct);

        if (result.UnknownCategories.Count > 0)
        {
            return ToolResult.Fail(
                "unknown_category",
                "Unknown categories: " + string.Join(", ", result.UnknownCategories) +
                ". Use list_categories to see available names.");
        }

        var cards = new List<ElementCard>();
        foreach (var element in result.Elements.Skip(spec.Offset).Take(spec.Limit))
        {
            ct.ThrowIfCancellationRequested();
            cards.Add(AgentElementCardFactory.Create(doc, element));
        }

        var payload = new
        {
            total = result.Total,
            offset = spec.Offset,
            truncated = result.Total > spec.Offset + cards.Count,
            elements = cards
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Matched " + result.Total + " element(s); returned " + cards.Count + ".");
    }
}