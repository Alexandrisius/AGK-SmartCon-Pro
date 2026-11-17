using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class GetViewSettingsTool : AgentReadToolBase
{
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
            "and hidden worksets. Use it to inspect a reference view before applying its settings " +
            "to other views.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""}},""required"":[""viewId""],""additionalProperties"":false}"
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
        return ToolResult.Ok(
            AgentJson.Serialize(settings),
            "Read settings of view '" + view.Name + "'.");
    }
}