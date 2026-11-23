using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class ListViewTemplatesTool : AgentReadToolBase
{
    private const int MaxTemplates = 500;

    private static readonly string[] ValueFields =
    {
        "scale", "detailLevel", "displayStyle", "discipline",
        "hiddenCategoryCount", "hiddenCategoryEditableCount", "controlledParameterCount"
    };

    public ListViewTemplatesTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_view_templates",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Description =
            "Lists view templates with id, name, view type and whether the template controls " +
            "view range, crop region and model visibility overrides. fields reads values too: " +
            "scale, detailLevel, displayStyle, discipline, hiddenCategoryCount, hiddenCategoryEditableCount, " +
            "controlledParameterCount — so an inventory of 45 templates is one call instead of " +
            "45 get_view_template_details. nameContains narrows the list. " +
            "Use it before applying or copying a template.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson + @"""nameContains"":{""type"":""string""},""fields"":{""type"":""array"",""description"":""Allowed: templateId,name,viewType,includesViewRange,includesCrop,includesVisibility,scale,detailLevel,displayStyle,discipline,hiddenCategoryCount,hiddenCategoryEditableCount,controlledParameterCount"",""items"":{""type"":""string""}}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var nameContains = AgentJson.GetString(args, "nameContains");
        var fields = AgentJson.GetStringArray(args, "fields");
        var wantsValues = fields.Any(field => ValueFields.Any(
            value => string.Equals(value, field, StringComparison.OrdinalIgnoreCase)));

        var ignored = new List<string>();
        var substituted = new Dictionary<string, string>(StringComparer.Ordinal);

        var collected = new List<(string Name, JsonElement Row)>();
        var totalMatched = 0;
        foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
        {
            ct.ThrowIfCancellationRequested();
            if (!view.IsTemplate)
                continue;

            // The cap applies to the output, not to the filter: stopping the scan
            // before nameContains made "template named X" return zero when the
            // library is larger than the cap and the match is later in id order.
            if (!string.IsNullOrWhiteSpace(nameContains)
                && view.Name.IndexOf(nameContains!, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            totalMatched++;
            if (collected.Count >= MaxTemplates)
                continue;

            var card = AgentViewSettingsReader.CreateTemplateCard(doc, view);
            object rowPayload = card;
            if (wantsValues)
            {
                var settings = AgentViewSettingsReader.Read(doc, view);
                rowPayload = new
                {
                    card.TemplateId,
                    card.Name,
                    card.ViewType,
                    card.IncludesViewRange,
                    card.IncludesCrop,
                    card.IncludesVisibility,
                    settings.Scale,
                    settings.DetailLevel,
                    settings.DisplayStyle,
                    settings.Discipline,
                    settings.HiddenCategoryCount,
                    settings.HiddenCategoryEditableCount,
                    controlledParameterCount = settings.ControlledParameters.Count
                };
            }

            var serialized = AgentJson.SerializeCard(rowPayload);
            collected.Add((view.Name, fields.Count == 0
                ? serialized
                : AgentCardProjection.Apply(serialized, fields, ignored, substituted)));
        }

        // Collector order is not deterministic; the model diffs inventories by name.
        collected.Sort((left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        var rows = collected.Select(item => item.Row).ToList();

        var payload = new
        {
            total = totalMatched,
            returned = rows.Count,
            truncated = totalMatched > rows.Count,
            ignoredFields = ignored.Count > 0 ? ignored : null,
            substitutedFields = substituted.Count > 0 ? substituted : null,
            availableFields = ignored.Count > 0
                ? AgentCardProjection.AvailableFields(AgentJson.SerializeCard(
                    new
                    {
                        templateId = 0L,
                        name = string.Empty,
                        viewType = string.Empty,
                        includesViewRange = false,
                        includesCrop = false,
                        includesVisibility = false,
                        scale = (int?)null,
                        detailLevel = (string?)null,
                        displayStyle = (string?)null,
                        discipline = (string?)null,
                        hiddenCategoryCount = 0,
                        hiddenCategoryEditableCount = 0,
                        controlledParameterCount = 0
                    }))
                : null,
            templates = rows
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Found " + totalMatched + " view template(s); returned " + rows.Count + ".");
    }
}
