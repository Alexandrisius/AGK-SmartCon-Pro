using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class GetCurrentViewInfoTool : AgentReadToolBase
{
    public GetCurrentViewInfoTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_current_view_info",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Description =
            "Returns the active view of the document: id, name, type, level, scale, detail level, " +
            "discipline and the applied view template. Use it when the user refers to " +
            "'this view' or 'the current view'.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var view = doc.ActiveView;
        if (view is null)
        {
            return ToolResult.Fail(
                "no_active_view",
                "The document has no active view. Ask the user to open a view in Revit.");
        }

        var templateId = view.ViewTemplateId;
        var hasTemplate = templateId != ElementId.InvalidElementId;
        var template = hasTemplate ? doc.GetElement(templateId) : null;
        var level = view.GenLevel;

        var payload = new
        {
            viewId = view.Id.GetValue(),
            uniqueId = view.UniqueId,
            name = view.Name,
            viewType = view.ViewType.ToString(),
            levelId = level is null ? (long?)null : level.Id.GetValue(),
            levelName = level?.Name,
            scale = view.Scale,
            detailLevel = view.DetailLevel.ToString(),
            discipline = view.Discipline.ToString(),
            isTemplate = view.IsTemplate,
            templateId = hasTemplate ? templateId.GetValue() : (long?)null,
            templateName = template?.Name,
            cropActive = view.CropBoxActive,
            cropVisible = view.CropBoxVisible
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Active view is '" + view.Name + "' (" + view.ViewType + ").");
    }
}