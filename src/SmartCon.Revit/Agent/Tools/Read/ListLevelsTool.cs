using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class ListLevelsTool : AgentReadToolBase
{
    public ListLevelsTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_levels",
        Description =
            "Lists all levels of the active project with ids, names, elevations in millimetres " +
            "and the building-story flag. Sorted by elevation.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{},""additionalProperties"":false,""documentId"":{""type"":""string"",""description"":""Background model routing: the documentId returned by open_model_background; omit for the active document""}}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var levels = new List<LevelCard>();
        foreach (var level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
        {
            ct.ThrowIfCancellationRequested();
            levels.Add(new LevelCard
            {
                LevelId = level.Id.GetValue(),
                Name = level.Name,
                ElevationMm = AgentUnitsFormat.ToMillimeters(level.Elevation),
                IsBuildingStory = level
                    .get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY)
                    ?.AsInteger() == 1
            });
        }

        levels.Sort((left, right) => left.ElevationMm.CompareTo(right.ElevationMm));
        var payload = new { total = levels.Count, levels };
        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Found " + levels.Count + " level(s).");
    }
}