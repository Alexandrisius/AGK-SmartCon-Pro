using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Tools.Project;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Lists the project parameter bindings of the active document
/// (doc.ParameterBindings): name, shared flag, GUID, binding type, categories,
/// parameter group and visibility. Duplicate names with different GUIDs are
/// reported as separate rows; an empty list is a valid answer.
/// </summary>
public sealed class ListProjectParametersTool : AgentReadToolBase
{
    private const int DefaultLimit = 50;

    public ListProjectParametersTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_project_parameters",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Description =
            "Lists the project parameters bound in the active project: name, isShared, guid, " +
            "bindingType (instance|type), categories, parameterGroup and visible. Same-name " +
            "definitions with different GUIDs are separate rows. Filters: " +
            "parameterNameContains, categoryContains; paged with limit/offset. Use it before " +
            "add_project_shared_parameter to check whether a parameter is already bound, and " +
            "after it to verify the binding. An empty list means the project has no project " +
            "parameters (valid, not an error).",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""parameterNameContains"":{""type"":""string"",""description"":""Parameter name filter (case-insensitive substring)""},""categoryContains"":{""type"":""string"",""description"":""Category name filter (case-insensitive substring, matches any bound category)""},""limit"":{""type"":""integer"",""default"":50,""maximum"":500},""offset"":{""type"":""integer"",""default"":0},""fields"":{""type"":""array"",""description"":""Allowed: name,isShared,guid,bindingType,categories,parameterGroup,visible,definitionId. Returned keys match the requested names."",""items"":{""type"":""string""}}},""additionalProperties"":false}"
    };

    private static readonly string[] AllowedFields =
    {
        "name", "isShared", "guid", "bindingType", "categories", "parameterGroup", "visible",
        "definitionId"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        if (doc.IsFamilyDocument)
        {
            return ToolResult.Fail(
                "not_a_project_document",
                "Project parameter bindings live in a project document; the active document " +
                "is a family. [Action: open the project and retry]");
        }

        var nameContains = AgentJson.GetString(args, "parameterNameContains");
        var categoryContains = AgentJson.GetString(args, "categoryContains");
        var limit = AgentPaging.NormalizeLimit(
            AgentJson.GetInt(args, "limit", DefaultLimit),
            DefaultLimit,
            AgentPaging.MaxLimit);
        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));
        var fields = AgentJson.GetStringArray(args, "fields");

        var rows = AgentProjectParameterReader.Collect(doc);
        ct.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            rows = rows.Where(row =>
                    row.Name.IndexOf(nameContains!, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(categoryContains))
        {
            rows = rows.Where(row => row.Categories.Any(category =>
                    category.IndexOf(categoryContains!, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();
        }

        var total = rows.Count;
        var page = AgentPaging.Slice(rows, offset, limit);
        var projected = new List<Dictionary<string, object?>>();
        foreach (var row in page)
        {
            projected.Add(Project(row, fields));
        }

        var nextOffset = offset + projected.Count;
        var payload = new Dictionary<string, object?>
        {
            ["total"] = total,
            ["parameters"] = projected,
            ["returned"] = projected.Count,
            ["offset"] = offset,
            ["nextOffset"] = nextOffset < total ? nextOffset : (int?)null,
            ["truncated"] = nextOffset < total,
            ["hint"] = "Pass the exact name to add_project_shared_parameter; it reports " +
                       "definition_not_found when the shared parameter file has no such " +
                       "definition (list_shared_parameters shows the file side)."
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            projected.Count == 0
                ? "The project has no project parameters matching the request."
                : "Read " + projected.Count + " of " + total + " project parameter binding(s).");
    }

    private static Dictionary<string, object?> Project(
        AgentProjectParameterReader.ProjectParameterRow row,
        IReadOnlyList<string> fields)
    {
        var full = new Dictionary<string, object?>
        {
            ["name"] = row.Name,
            ["isShared"] = row.IsShared,
            ["guid"] = row.Guid?.ToString("D"),
            ["bindingType"] = row.BindingType,
            ["categories"] = row.Categories,
            ["parameterGroup"] = row.ParameterGroup,
            ["visible"] = row.Visible,
            ["definitionId"] = row.DefinitionId
        };

        if (fields.Count == 0)
            return full;

        var selected = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            if (full.TryGetValue(field, out var value))
                selected[field] = value;
        }

        return selected;
    }
}
