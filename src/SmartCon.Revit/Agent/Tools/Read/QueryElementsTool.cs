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
            "Condition parameter: a parameter name (built-in, project or shared; add " +
            "includeTypeParameters=true to search type parameters), a BuiltInParameter id " +
            "(ALL_MODEL_MARK), or a structural field: name (element, family or type name), family, " +
            "type, category (OST_ name or display name), level. Structural fields are reserved " +
            "words checked before any parameter, support string operators only and need no " +
            "includeTypeParameters; a project parameter with the same name is not reachable in " +
            "conditions. A parameter missing on every scanned element returns unknown_parameter " +
            "instead of an empty list. " +
            "String comparisons are case-insensitive. For numeric conditions pass unit (mm recommended); " +
            "without unit the project display unit for that parameter is used. " +
            "Default limit 50, max 200; use offset/nextOffset to paginate. " +
            "Broad queries without categories/levelIds/typeIds/conditions/viewId are rejected " +
            "when limit > 50. fields=[\"elementId\",\"name\"] projects each card; every card also " +
            "carries mark, comments and phaseCreated (built-in values, independent of the UI " +
            "language), projectable directly: fields=[\"mark\",\"phaseCreated\"]. Parameter " +
            "conditions accept the same aliases (parameter=\"mark\", \"comments\", \"phaseCreated\").",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""documentId"":{""type"":""string"",""description"":""Background model routing: the documentId returned by open_model_background; omit for the active document""},""categories"":{""type"":""array"",""items"":{""type"":""string""}},""levelIds"":{""type"":""array"",""items"":{""type"":""integer""}},""typeIds"":{""type"":""array"",""items"":{""type"":""integer""}},""viewId"":{""type"":""integer""},""roomId"":{""type"":""integer""},""conditions"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""parameter"":{""type"":""string""},""op"":{""type"":""string"",""enum"":[""eq"",""neq"",""gt"",""gte"",""lt"",""lte"",""contains"",""startsWith"",""endsWith"",""in"",""notIn"",""exists"",""notExists""]},""value"":{},""unit"":{""type"":""string""}},""required"":[""parameter"",""op""],""additionalProperties"":false}},""logic"":{""type"":""string"",""enum"":[""and"",""or""],""default"":""and""},""includeTypeParameters"":{""type"":""boolean"",""default"":false},""limit"":{""type"":""integer"",""default"":50,""maximum"":200},""offset"":{""type"":""integer"",""default"":0},""fields"":{""type"":""array"",""items"":{""type"":""string""}}},""additionalProperties"":false}"
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

        var broadQuery = spec.Categories.Count == 0
                         && spec.LevelIds.Count == 0
                         && spec.TypeIds.Count == 0
                         && spec.Conditions.Count == 0
                         && spec.ViewId is null
                         && spec.RoomId is null;
        if (broadQuery && spec.Limit > 50)
        {
            return ToolResult.Fail(
                "query_too_broad",
                "This query has no filters and requests " + spec.Limit + " elements. " +
                "Add at least one of categories, levelIds, typeIds, conditions or viewId, " +
                "or reduce limit to 50. Use summarize_elements for counts instead of a full dump.");
        }

        if (spec.ViewId is { } viewId && doc.GetElement(ElementIdCompat.Create(viewId)) is not View)
        {
            return ToolResult.Fail(
                "invalid_view",
                "viewId " + viewId + " does not identify a view in this document.");
        }

        if (spec.RoomId is { } roomId
            && doc.GetElement(ElementIdCompat.Create(roomId)) is not Autodesk.Revit.DB.Architecture.Room)
        {
            return ToolResult.Fail(
                "invalid_room",
                "roomId " + roomId + " does not identify a room in this document. " +
                "Use query_elements(categories=[\"OST_Rooms\"]) to list rooms.");
        }

        var query = new AgentElementQuery(doc);
        AgentQueryResult result;
        try
        {
            result = query.Execute(spec, spec.Offset + spec.Limit, ct);
        }
        catch (AgentQueryViewException ex)
        {
            return ToolResult.Fail(
                "invalid_view",
                ex.Message +
                " [Action: pass a graphical view (plan, section, 3D); sheets and templates " +
                "cannot be searched]");
        }
        catch (ArgumentException ex)
        {
            // Wrong-dimension or unknown unit in a numeric condition.
            return ToolResult.Fail("invalid_unit", ex.Message);
        }

        if (result.UnknownCategories.Count > 0)
        {
            return ToolResult.Fail(
                "unknown_category",
                "Unknown categories: " + string.Join(", ", result.UnknownCategories) +
                AgentCategoryResolver.SuggestSuffix(doc, result.UnknownCategories[0]) +
                ". Use list_categories to see available names.");
        }

        if (result.UnknownParameterFailure() is { } unknownParameter)
        {
            return unknownParameter;
        }

        var fields = AgentJson.GetStringArray(args, "fields");
        var ignored = new List<string>();
        var cards = new List<object>();
        IReadOnlyList<string> available = Array.Empty<string>();
        foreach (var element in result.Elements.Skip(spec.Offset).Take(spec.Limit))
        {
            ct.ThrowIfCancellationRequested();
            var card = AgentElementCardFactory.Create(doc, element);
            var serialized = fields.Count == 0
                ? AgentJson.Serialize(card)
                : AgentJson.SerializeCard(card);
            if (available.Count == 0 && fields.Count > 0)
                available = AgentCardProjection.AvailableFields(serialized);

            cards.Add(fields.Count == 0
                ? (object)card
                : AgentCardProjection.Apply(serialized, fields, ignored));
        }

        var truncated = result.Total > spec.Offset + cards.Count;
        int? nextOffset = truncated ? spec.Offset + cards.Count : null;

        var payload = new
        {
            total = result.Total,
            offset = spec.Offset,
            returned = cards.Count,
            truncated,
            nextOffset,
            // A view-scoped collector sees only elements visible in that view:
            // hidden categories, filters, worksets and crop are excluded from total.
            scope = spec.ViewId is null ? "document" : "viewVisible",
            scopeNote = spec.ViewId is null
                ? null
                : "Only elements visible in the view are counted: categories, filters " +
                  "and worksets hidden in it, and elements outside its crop, are excluded.",
            ignoredFields = ignored.Count > 0 ? ignored : null,
            availableFields = ignored.Count > 0 ? available : null,
            hint = truncated
                ? "More elements match. Call query_elements again with offset=" + nextOffset +
                  " or add filters to narrow the result."
                : null,
            elements = cards
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Matched " + result.Total + " element(s); returned " + cards.Count + "." +
            (truncated ? " More available: use offset=" + nextOffset + "." : string.Empty));
    }
}