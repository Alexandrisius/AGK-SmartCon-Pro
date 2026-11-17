using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class GetViewTemplateDetailsTool : AgentReadToolBase
{
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
            "Use it to verify what will change when the template is applied to a view.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""templateId"":{""type"":""integer""}},""required"":[""templateId""],""additionalProperties"":false}"
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

        var payload = new
        {
            template = AgentViewSettingsReader.CreateTemplateCard(doc, view),
            settings = AgentViewSettingsReader.Read(doc, view)
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Read view template '" + view.Name + "'.");
    }
}