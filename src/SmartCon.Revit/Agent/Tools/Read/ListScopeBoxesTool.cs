using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Lists scope boxes (#351). A scope box is a plain Element of category
/// OST_VolumeOfInterest with no dedicated API class: it cannot be created or
/// resized through the API (only read and assigned to views), so this tool is
/// the inventory an assignment needs. Bounds come from the element bounding
/// box — for a rotated scope box that is the enclosing world-aligned box.
/// </summary>
public sealed class ListScopeBoxesTool : AgentReadToolBase
{
    private const int MaxScopeBoxes = 500;

    public ListScopeBoxesTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_scope_boxes",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Description =
            "Lists scope boxes with id, name and extents (minMm/maxMm, model millimetres; " +
            "a rotated scope box is reported by its enclosing box). Scope boxes cannot be " +
            "created or resized through the Revit API — the user draws them in the UI; this " +
            "list is what assign_scope_box consumes. nameContains narrows by name. An empty " +
            "list means the project has no scope boxes yet.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson + @"""nameContains"":{""type"":""string""}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var nameContains = AgentJson.GetString(args, "nameContains");

        var collected = new List<(string Name, object Card)>();
        var totalMatched = 0;
        foreach (var element in new FilteredElementCollector(doc)
                     .OfCategory(BuiltInCategory.OST_VolumeOfInterest)
                     .WhereElementIsNotElementType())
        {
            ct.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(nameContains)
                && element.Name.IndexOf(nameContains!, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            totalMatched++;
            if (collected.Count >= MaxScopeBoxes)
                continue;

            collected.Add((element.Name, CreateCard(element)));
        }

        collected.Sort((left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        var payload = new
        {
            total = totalMatched,
            returned = collected.Count,
            truncated = totalMatched > collected.Count,
            scopeBoxes = collected.Select(item => item.Card).ToList()
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Found " + totalMatched + " scope box(es).");
    }

    private static object CreateCard(Element element)
    {
        double[]? minMm = null;
        double[]? maxMm = null;
        try
        {
            var box = element.get_BoundingBox(null);
            if (box is not null)
            {
                minMm = new[]
                {
                    AgentUnitsFormat.ToMillimeters(box.Min.X),
                    AgentUnitsFormat.ToMillimeters(box.Min.Y),
                    AgentUnitsFormat.ToMillimeters(box.Min.Z)
                };
                maxMm = new[]
                {
                    AgentUnitsFormat.ToMillimeters(box.Max.X),
                    AgentUnitsFormat.ToMillimeters(box.Max.Y),
                    AgentUnitsFormat.ToMillimeters(box.Max.Z)
                };
            }
        }
        catch (Exception)
        {
            // A scope box without readable geometry still has an id and a name.
        }

        return new
        {
            scopeBoxId = element.Id.GetValue(),
            name = element.Name,
            minMm,
            maxMm
        };
    }
}
