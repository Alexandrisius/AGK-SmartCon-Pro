using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class ListViewsTool : AgentReadToolBase
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 500;

    public ListViewsTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_views",
        Description =
            "Lists project views (not sheets and not view templates by default). " +
            "Filters: view types (FloorPlan, CeilingPlan, Section, ThreeD, Elevation, " +
            "DraftingView, Legend, AreaPlan, StructuralPlan, Detail), level ids, name substring. " +
            "Returns view id, name, type, level, template, sheet number. Default limit 50, max 500. " +
            "summaryOnly=true returns counts by view type and by level instead of cards. " +
            "fields=[\"viewId\",\"name\"] projects each card onto the listed properties.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""types"":{""type"":""array"",""items"":{""type"":""string""}},""levelIds"":{""type"":""array"",""items"":{""type"":""integer""}},""nameContains"":{""type"":""string""},""includeTemplates"":{""type"":""boolean"",""default"":false},""summaryOnly"":{""type"":""boolean"",""default"":false},""fields"":{""type"":""array"",""items"":{""type"":""string""}},""limit"":{""type"":""integer"",""default"":50,""maximum"":500}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var requestedTypes = AgentJson.GetStringArray(args, "types");
        var levelIds = AgentJson.GetLongArray(args, "levelIds");
        var nameContains = AgentJson.GetString(args, "nameContains");
        var includeTemplates = AgentJson.GetBool(args, "includeTemplates", false);
        var summaryOnly = AgentJson.GetBool(args, "summaryOnly", false);
        var fields = AgentJson.GetStringArray(args, "fields");
        var limit = Math.Max(1, Math.Min(AgentJson.GetInt(args, "limit", DefaultLimit), MaxLimit));

        var matched = new List<ViewCard>();
        foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
        {
            ct.ThrowIfCancellationRequested();

            if (!IsSupportedView(view))
                continue;
            if (view.IsTemplate && !includeTemplates)
                continue;
            if (!MatchesType(view, requestedTypes))
                continue;
            if (!MatchesLevel(view, levelIds))
                continue;
            if (!MatchesName(view, nameContains))
                continue;

            matched.Add(CreateCard(doc, view));
        }

        matched.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        var total = matched.Count;

        if (summaryOnly)
        {
            var summaryPayload = new
            {
                total,
                summaryOnly = true,
                byType = GroupBy(matched, view => view.ViewType),
                byLevel = GroupBy(matched, view => view.LevelName ?? "(no level)")
            };

            return ToolResult.Ok(
                AgentJson.Serialize(summaryPayload),
                "Matched " + total + " view(s); summary by type and level.");
        }

        var views = matched.Take(limit).ToList();

        if (fields.Count > 0)
        {
            var projected = new List<System.Text.Json.JsonElement>(views.Count);
            foreach (var card in views)
                projected.Add(AgentCardProjection.Apply(AgentJson.Serialize(card), fields));

            var projectedPayload = new
            {
                total,
                returned = projected.Count,
                truncated = total > projected.Count,
                views = projected
            };

            return ToolResult.Ok(
                AgentJson.Serialize(projectedPayload),
                "Matched " + total + " view(s); returned " + projected.Count + "." +
                (total > projected.Count
                    ? " The result is truncated — narrow the filters or raise the limit."
                    : string.Empty));
        }

        var payload = new
        {
            total,
            returned = views.Count,
            truncated = total > views.Count,
            views
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Matched " + total + " view(s); returned " + views.Count + "." +
            (total > views.Count
                ? " The result is truncated — narrow the filters or raise the limit."
                : string.Empty));
    }

    private static IReadOnlyList<object> GroupBy(
        IReadOnlyList<ViewCard> cards,
        Func<ViewCard, string> keySelector)
    {
        var groups = new List<(string Key, int Count)>();
        foreach (var group in cards.GroupBy(keySelector, StringComparer.OrdinalIgnoreCase))
            groups.Add((group.Key, group.Count()));

        groups.Sort((left, right) => right.Count.CompareTo(left.Count));

        var result = new List<object>(groups.Count);
        foreach (var group in groups)
            result.Add(new { key = group.Key, count = group.Count });

        return result;
    }

    private static bool IsSupportedView(View view)
    {
        return view.ViewType != ViewType.DrawingSheet
               && view.ViewType != ViewType.Internal
               && view.ViewType != ViewType.ProjectBrowser
               && view.ViewType != ViewType.SystemBrowser
               && view.ViewType != ViewType.Undefined;
    }

    private static bool MatchesType(View view, IReadOnlyList<string> requestedTypes)
    {
        if (requestedTypes.Count == 0)
            return true;

        var actual = Normalize(view.ViewType.ToString());
        foreach (var requested in requestedTypes)
        {
            if (actual == Normalize(requested))
                return true;
        }

        return false;
    }

    private static bool MatchesLevel(View view, IReadOnlyList<long> levelIds)
    {
        if (levelIds.Count == 0)
            return true;

        var level = view.GenLevel;
        if (level is null)
            return false;

        var id = level.Id.GetValue();
        return levelIds.Contains(id);
    }

    private static bool MatchesName(View view, string? nameContains)
    {
        if (string.IsNullOrWhiteSpace(nameContains))
            return true;

#if NET8_0_OR_GREATER
        return view.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase);
#else
        return view.Name.IndexOf(nameContains!, StringComparison.OrdinalIgnoreCase) >= 0;
#endif
    }

    private static ViewCard CreateCard(Document doc, View view)
    {
        var level = view.GenLevel;
        var templateId = view.ViewTemplateId;
        var hasTemplate = templateId != ElementId.InvalidElementId;
        var template = hasTemplate ? doc.GetElement(templateId) : null;
        var sheetNumber = view.get_Parameter(BuiltInParameter.VIEWER_SHEET_NUMBER)?.AsString();
        if (sheetNumber is "—" or "-" or "---" or "")
            sheetNumber = null;

        return new ViewCard
        {
            ViewId = view.Id.GetValue(),
            UniqueId = view.UniqueId,
            Name = view.Name,
            ViewType = view.ViewType.ToString(),
            LevelId = level is null ? null : level.Id.GetValue(),
            LevelName = level?.Name,
            ElevationMm = level is null
                ? null
                : AgentUnitsFormat.ToMillimeters(level.Elevation),
            TemplateId = hasTemplate ? templateId.GetValue() : null,
            TemplateName = hasTemplate ? template?.Name : null,
            IsTemplate = view.IsTemplate,
            IsOnSheet = sheetNumber is not null,
            SheetNumber = sheetNumber
        };
    }

    private static string Normalize(string value)
    {
        var normalized = value.Replace("_", string.Empty)
            .Replace(" ", string.Empty)
            .Replace("-", string.Empty)
            .ToLowerInvariant();

        return normalized switch
        {
            "3d" => "threed",
            "plan" => "floorplan",
            "ceiling" => "ceilingplan",
            "structural" => "structuralplan",
            "drafting" => "draftingview",
            "area" => "areaplan",
            _ => normalized
        };
    }
}