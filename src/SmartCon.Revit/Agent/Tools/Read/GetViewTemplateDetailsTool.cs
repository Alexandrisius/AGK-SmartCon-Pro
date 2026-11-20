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
            "fields=[\"settings\"] is also compact: the hidden-category and controlled-parameter " +
            "lists are replaced by counts — ask for settings.hiddenCategories or " +
            "controlledParameters explicitly to get the lists. " +
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
            var full = new
            {
                kind = "template",
                note = "settings describes the values stored in the template. " +
                       "settings.hasTemplate=false and an absent settings.templateId are " +
                       "expected: a template cannot use another template.",
                template = card,
                settings
            };
            return ToolResult.Ok(
                AgentJson.Serialize(full),
                "Read the full settings of view template '" + view.Name + "'.");
        }

        var summary = new
        {
            kind = "template",
            note = "settings describes the values stored in the template; " +
                   "settings.hasTemplate=false is expected for a template.",
            template = card,
            settings = AgentViewSettingsReader.CreateSummary(settings)
        };
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
        var includeAllSettings = false;
        var includeAllCard = false;

        foreach (var field in fields)
        {
            if (string.Equals(field, "settings", StringComparison.OrdinalIgnoreCase))
            {
                includeAllSettings = true;
                continue;
            }

            if (string.Equals(field, "template", StringComparison.OrdinalIgnoreCase))
            {
                includeAllCard = true;
                continue;
            }

            if (field.StartsWith("settings.", StringComparison.OrdinalIgnoreCase))
            {
                projectSettingsFields.Add(field.Substring("settings.".Length));
                continue;
            }

            if (Resolves(settingsJson, field))
                projectSettingsFields.Add(field);
            else
                projectCardFields.Add(field);
        }

        var node = new JsonObject();
        if (includeAllSettings)
        {
            // "settings" is a compact projection: the two long lists are replaced by
            // their counts and stay available as explicit fields (A6).
            node["settings"] = CompactSettings(settingsJson);
        }

        if (projectSettingsFields.Count > 0)
        {
            var explicitSettings = ToNode(AgentCardProjection.Apply(
                settingsJson, projectSettingsFields, ignored));
            if (includeAllSettings
                && node["settings"] is JsonObject settingsNode
                && explicitSettings is JsonObject explicitNode)
            {
                // fields=["settings","settings.hiddenCategories"] must keep the lists
                // that the caller explicitly asked for.
                foreach (var pair in explicitNode.ToList())
                {
                    explicitNode.Remove(pair.Key);
                    settingsNode[pair.Key] = pair.Value;
                }
            }
            else
            {
                node["settings"] = explicitSettings;
            }
        }

        if (includeAllCard)
        {
            node["template"] = ToNode(AgentJson.SerializeCard(card));
        }
        else if (projectCardFields.Count > 0)
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

    /// <summary>
    /// Replaces hiddenCategories / controlledParameters / nonControlledParameters with
    /// their counts. Their lists made "fields=[\"settings\"]" ~30 KB per template
    /// (external run, A6); request them explicitly when the values are needed.
    /// </summary>
    private static JsonNode? CompactSettings(JsonElement settingsJson)
    {
        if (JsonNode.Parse(settingsJson.GetRawText()) is not JsonObject node)
            return null;

        if (node["hiddenCategories"] is JsonArray hidden)
        {
            node["hiddenCategoryCount"] = hidden.Count;
            node.Remove("hiddenCategories");
        }

        if (node["controlledParameters"] is JsonArray controlled)
        {
            node["controlledParameterCount"] = controlled.Count;
            node.Remove("controlledParameters");
        }

        if (node["nonControlledParameters"] is JsonArray nonControlled)
        {
            node["nonControlledParameterCount"] = nonControlled.Count;
            node.Remove("nonControlledParameters");
        }

        return node;
    }

    private static JsonArray ToArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }
}
