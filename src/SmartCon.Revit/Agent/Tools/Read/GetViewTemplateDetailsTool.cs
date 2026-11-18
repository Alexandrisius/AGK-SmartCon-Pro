using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class GetViewTemplateDetailsTool : AgentReadToolBase
{
    private const string CompactHint =
        "Compact summary. Pass fields=[\"viewRange\",\"crop\",\"hiddenCategories\",...] for selected " +
        "settings or compact=false for the full payload.";

    public GetViewTemplateDetailsTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_view_template_details",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Description =
            "Returns the settings stored in a view template (view range, crop, hidden categories, " +
            "worksets) plus the exact list of parameters controlled and not controlled by it. " +
            "Without fields and with compact=true (default) it returns a compact summary; pass " +
            "fields to request specific settings or compact=false for the full payload. " +
            "Use it to verify what will change when the template is applied to a view.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""templateId"":{""type"":""integer""},""fields"":{""type"":""array"",""description"":""Allowed: templateId,name,viewType,includesViewRange,includesCrop,includesVisibility,settings. Use settings.scale / settings.hiddenCategories for template values."",""items"":{""type"":""string""}},""compact"":{""type"":""boolean"",""default"":true}},""required"":[""templateId""],""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var templateId = AgentJson.GetLong(args, "templateId", 0);
        if (templateId == 0)
            return ToolResult.Fail("invalid_arguments", "templateId is required.");

        if (doc.GetElement(ElementIdCompat.Create(templateId)) is not View view
            || !view.IsTemplate)
        {
            return ToolResult.Fail(
                "template_not_found",
                "No view template with id " + templateId + " exists in this document.");
        }

        var card = AgentViewSettingsReader.CreateTemplateCard(doc, view);
        var settings = AgentViewSettingsReader.Read(doc, view);
        var fields = AgentJson.GetStringArray(args, "fields");

        if (fields.Count > 0)
        {
            var payload = Project(card, settings, fields);
            return ToolResult.Ok(
                payload,
                "Read the requested settings of view template '" + view.Name + "'.");
        }

        if (!AgentJson.GetBool(args, "compact", true))
        {
            var full = new { template = card, settings };
            return ToolResult.Ok(
                AgentJson.Serialize(full),
                "Read the full settings of view template '" + view.Name + "'.");
        }

        var summary = new { template = card, settings = AgentViewSettingsReader.CreateSummary(settings) };
        return ToolResult.Ok(
            Compact(summary),
            "Read a compact summary of view template '" + view.Name + "'. " + CompactHint);
    }

    /// <summary>
    /// Fields may address either the template card (templateId, includesViewRange, …)
    /// or the stored settings (viewRange, crop, scale, …). Both projections are
    /// merged into one payload; unresolvable fields are reported back.
    /// </summary>
    private static JsonElement Project(
        ViewTemplateCard card,
        ViewSettings settings,
        IReadOnlyList<string> fields)
    {
        var ignored = new List<string>();
        var settingsJson = AgentJson.SerializeCard(settings);
        var projectSettingsFields = new List<string>();
        var projectCardFields = new List<string>();

        foreach (var field in fields)
        {
            if (Resolves(settingsJson, field))
                projectSettingsFields.Add(field);
            else
                projectCardFields.Add(field);
        }

        var node = new JsonObject();
        if (projectSettingsFields.Count > 0)
        {
            node["settings"] = ToNode(AgentCardProjection.Apply(
                settingsJson, projectSettingsFields, ignored));
        }

        if (projectCardFields.Count > 0)
        {
            var cardJson = AgentJson.SerializeCard(card);
            var projectCard = AgentCardProjection.Apply(cardJson, projectCardFields, ignored);
            if (projectCard.ValueKind == JsonValueKind.Object
                && projectCard.EnumerateObject().Any())
            {
                node["template"] = ToNode(projectCard);
            }
        }

        if (ignored.Count > 0)
        {
            node["ignoredFields"] = ToArray(ignored);
            node["availableSettingsFields"] = ToArray(
                AgentCardProjection.AvailableFields(settingsJson));
        }

        return JsonSerializer.SerializeToElement(node);
    }

    private static bool Resolves(JsonElement settings, string field)
    {
        var probe = AgentCardProjection.Apply(settings, new[] { field });
        return probe.ValueKind == JsonValueKind.Object && probe.EnumerateObject().Any();
    }

    private static JsonElement Compact(object summary)
    {
        var node = JsonNode.Parse(AgentJson.Serialize(summary).GetRawText()) as JsonObject
                   ?? new JsonObject();
        node["hint"] = CompactHint;
        return JsonSerializer.SerializeToElement(node);
    }

    private static JsonNode? ToNode(JsonElement element) =>
        JsonNode.Parse(element.GetRawText());

    private static JsonArray ToArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }
}
