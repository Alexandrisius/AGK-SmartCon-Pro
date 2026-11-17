using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class ListViewTemplatesTool : AgentReadToolBase
{
    private const int MaxTemplates = 500;

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
            "view range, crop region and model visibility overrides. " +
            "Use it before applying or copying a template.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var templates = new List<ViewTemplateCard>();
        foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
        {
            ct.ThrowIfCancellationRequested();
            if (!view.IsTemplate)
                continue;

            templates.Add(AgentViewSettingsReader.CreateTemplateCard(doc, view));
            if (templates.Count >= MaxTemplates)
                break;
        }

        templates.Sort((left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        var payload = new { total = templates.Count, templates };
        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Found " + templates.Count + " view template(s).");
    }
}