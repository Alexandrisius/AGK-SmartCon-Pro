using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class GetSelectedElementsTool : AgentReadToolBase
{
    private const int MaxElements = 100;
    private readonly IAgentSelectionProvider _selectionProvider;

    public GetSelectedElementsTool(
        IRevitContext revitContext,
        IAgentSelectionProvider selectionProvider)
        : base(revitContext)
    {
        _selectionProvider = selectionProvider
            ?? throw new ArgumentNullException(nameof(selectionProvider));
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_selected_elements",
        Description =
            "Returns compact cards for the elements currently selected in the active Revit view. " +
            "Use it when the user refers to 'selected elements' or 'this element'.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var ids = _selectionProvider.GetSelectedElementIds();
        if (ids is null)
        {
            return ToolResult.Fail(
                "no_active_ui_document",
                "The active UI document or selection is unavailable.");
        }

        var total = ids.Count;
        var cards = new List<ElementCard>();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            if (cards.Count >= MaxElements)
                break;

            var element = doc.GetElement(id);
            if (element is not null)
                cards.Add(AgentElementCardFactory.Create(doc, element));
        }

        var payload = new { total, truncated = total > cards.Count, elements = cards };
        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            total == 0
                ? "Nothing is selected. Select one or more elements in Revit, then call " +
                  "get_selected_elements again."
                : "Selected " + total + " element(s); returned " + cards.Count + ".");
    }
}