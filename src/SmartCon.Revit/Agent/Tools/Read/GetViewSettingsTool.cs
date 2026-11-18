using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class GetViewSettingsTool : AgentReadToolBase
{
    private const string CompactHint =
        "Compact summary. Pass fields=[\"scale\",\"viewRange\",\"crop\",\"hiddenCategories\",...] " +
        "for selected settings or compact=false for the full payload.";

    public GetViewSettingsTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_view_settings",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Description =
            "Returns the current settings of a view: view template, scale, detail level, discipline, " +
            "plan view range (levels and offsets in mm), crop region box (mm), hidden categories " +
            "and hidden worksets. Without fields and with compact=true (default) it returns a compact " +
            "summary (template, scale, counts); pass fields to request specific settings or " +
            "compact=false for the full payload. Use it to inspect a reference view before applying " +
            "its settings to other views.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""},""fields"":{""type"":""array"",""items"":{""type"":""string""}},""compact"":{""type"":""boolean"",""default"":true}},""required"":[""viewId""],""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var viewId = AgentJson.GetLong(args, "viewId", 0);
        if (viewId == 0)
            return ToolResult.Fail("invalid_arguments", "viewId is required.");

        if (doc.GetElement(ElementIdCompat.Create(viewId)) is not View view)
        {
            return ToolResult.Fail(
                "view_not_found",
                "No view with id " + viewId + " exists in this document.");
        }

        var settings = AgentViewSettingsReader.Read(doc, view);
        var fields = AgentJson.GetStringArray(args, "fields");

        if (fields.Count > 0)
        {
            var payload = Project(AgentJson.SerializeCard(settings), fields);
            return ToolResult.Ok(
                payload,
                "Read the requested settings of view '" + view.Name + "'.");
        }

        if (!AgentJson.GetBool(args, "compact", true))
        {
            return ToolResult.Ok(
                AgentJson.Serialize(settings),
                "Read the full settings of view '" + view.Name + "'.");
        }

        var summary = AgentViewSettingsReader.CreateSummary(settings);
        return ToolResult.Ok(
            Compact(summary),
            "Read a compact summary of view '" + view.Name + "'. " + CompactHint);
    }

    private static JsonElement Project(JsonElement full, IReadOnlyList<string> fields)
    {
        var ignored = new List<string>();
        var substituted = new Dictionary<string, string>(StringComparer.Ordinal);
        var projected = AgentCardProjection.Apply(full, fields, ignored, substituted);
        if (ignored.Count == 0 && substituted.Count == 0)
            return projected;

        var node = JsonNode.Parse(projected.GetRawText()) as JsonObject ?? new JsonObject();
        if (ignored.Count > 0)
        {
            node["ignoredFields"] = ToArray(ignored);
            node["availableFields"] = ToArray(AgentCardProjection.AvailableFields(full));
        }

        if (substituted.Count > 0)
            node["substitutedFields"] = JsonSerializer.SerializeToNode(substituted);

        return JsonSerializer.SerializeToElement(node);
    }

    private static JsonElement Compact(ViewSettingsSummary summary)
    {
        var node = JsonNode.Parse(AgentJson.Serialize(summary).GetRawText()) as JsonObject
                   ?? new JsonObject();
        node["hint"] = CompactHint;
        return JsonSerializer.SerializeToElement(node);
    }

    private static JsonArray ToArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }
}
