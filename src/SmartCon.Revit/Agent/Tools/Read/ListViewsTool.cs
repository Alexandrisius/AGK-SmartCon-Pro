using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class ListViewsTool : AgentReadToolBase
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 500;

    public ListViewsTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_views",
        Description =
            "Lists project views (not sheets and not view templates by default). totalAll/totalTemplates are always reported next to total, so includeTemplates does not change the inventory silently. " +
            "Filters: view types (FloorPlan, CeilingPlan, Section, ThreeD, Elevation, " +
            "DraftingView, Legend, AreaPlan, StructuralPlan, Detail, Schedule — " +
            "specifications; any ViewType name is accepted), level ids, name substring, " +
            "browser folder path substring. Returns view id, name, type, level, template, " +
            "sheet number, browserFolder (path), browserFolderPath (segments), " +
            "browserFolderParameters (grouping parameter per segment: name/shared/built-in), " +
            "inBrowser and section — the value of the project parameter 'Раздел' on the view " +
            "(null when the project has no such parameter; it is read, never guessed from the " +
            "view name). For schedules (specifications) isOnSheet/sheetNumber account for " +
            "ScheduleSheetInstance placements (a placed schedule carries no " +
            "VIEWER_SHEET_NUMBER; several sheets are listed comma-separated). " +
            "Default limit 200, max 500; offset/nextOffset page through a larger " +
            "inventory. " +
            "summaryOnly=true returns counts by view type, level and browser folder instead of cards. " +
            "fields=[\"viewId\",\"name\",\"type\",\"level\",\"folder\"] projects each card onto the " +
            "listed properties; aliases and unknown names are reported in substitutedFields / " +
            "ignoredFields with availableFields.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""documentId"":{""type"":""string"",""description"":""Background model routing: the documentId returned by open_model_background; omit for the active document""},""types"":{""type"":""array"",""items"":{""type"":""string""}},""levelIds"":{""type"":""array"",""items"":{""type"":""integer""}},""viewIds"":{""type"":""array"",""description"":""Return only these view ids (with totalAll/totalTemplates still counting the whole inventory)."",""items"":{""type"":""integer""}},""nameContains"":{""type"":""string""},""folders"":{""type"":""array"",""items"":{""type"":""string""}},""includeTemplates"":{""type"":""boolean"",""default"":false},""summaryOnly"":{""type"":""boolean"",""default"":false},""fields"":{""type"":""array"",""description"":""Allowed: viewId,id,name,type|viewType,level|levelName,levelId,elevation,template|templateName,templateId,sheet|sheetNumber,isTemplate,isOnSheet,uniqueId,folder|browserFolder,folderPath|browserFolderPath,folderParameters|browserFolderParameters,inBrowser,section. folderParameters lists each grouping level with parameterId/parameterName/isShared/isBuiltIn; section is the project parameter 'Раздел' value (null when absent). Returned keys match the requested names."",""items"":{""type"":""string""}},""limit"":{""type"":""integer"",""default"":200,""maximum"":500},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var requestedTypes = AgentJson.GetStringArray(args, "types");
        var levelIds = AgentJson.GetLongArray(args, "levelIds");
        var viewIds = AgentJson.GetLongArray(args, "viewIds");
        var nameContains = AgentJson.GetString(args, "nameContains");
        var folders = AgentJson.GetStringArray(args, "folders");
        var includeTemplates = AgentJson.GetBool(args, "includeTemplates", false);
        var summaryOnly = AgentJson.GetBool(args, "summaryOnly", false);
        var fields = AgentJson.GetStringArray(args, "fields");
        var limit = Math.Max(1, Math.Min(AgentJson.GetInt(args, "limit", DefaultLimit), MaxLimit));
        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));

        BrowserOrganization? browserOrganization = null;
        try
        {
            browserOrganization = BrowserOrganization.GetCurrentBrowserOrganizationForViews(doc);
        }
        catch (Exception)
        {
            // Older projects or a broken browser organization: folder data is optional.
        }

        // C8-03: a schedule placed on a sheet carries no VIEWER_SHEET_NUMBER —
        // its placements live in ScheduleSheetInstance elements.
        var scheduleSheetNumbers = AgentScheduleReader.MapSheetNumbers(doc);

        var allMatched = new List<ViewCard>();
        foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
        {
            ct.ThrowIfCancellationRequested();

            if (!IsSupportedView(view))
                continue;
            if (viewIds.Count > 0 && !viewIds.Contains(view.Id.GetValue()))
                continue;
            if (!MatchesType(view, requestedTypes))
                continue;
            if (!MatchesLevel(view, levelIds))
                continue;
            if (!MatchesName(view, nameContains))
                continue;

            var card = CreateCard(doc, view, browserOrganization, scheduleSheetNumbers);
            if (!MatchesFolder(card, folders))
                continue;

            allMatched.Add(card);
        }

        // includeTemplates changes total (117 vs 72) and that trap cost a round:
        // both numbers are always reported (external run, B3).
        var totalAll = allMatched.Count;
        var totalTemplates = allMatched.Count(card => card.IsTemplate);
        var matched = includeTemplates
            ? allMatched
            : allMatched.Where(card => !card.IsTemplate).ToList();

        matched.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        var total = matched.Count;

        if (summaryOnly)
        {
            var summaryPayload = new
            {
                total,
                totalAll,
                totalTemplates,
                summaryOnly = true,
                browserOrganization = browserOrganization?.Name,
                byType = GroupBy(matched, view => view.ViewType),
                byLevel = GroupBy(matched, view => view.LevelName ?? "(no level)"),
                byFolder = GroupBy(
                    matched,
                    view => string.IsNullOrWhiteSpace(view.BrowserFolder)
                        ? "(no folder)"
                        : view.BrowserFolder!),
                folderTree = BuildFolderTree(matched)
            };

            return ToolResult.Ok(
                AgentJson.Serialize(summaryPayload),
                "Matched " + total + " view(s); summary by type and level.");
        }

        var views = matched.Skip(offset).Take(limit).ToList();
        var hasMore = offset + views.Count < total;
        var nextOffset = hasMore ? offset + views.Count : (int?)null;

        if (fields.Count > 0)
        {
            var projected = new List<System.Text.Json.JsonElement>(views.Count);
            var ignored = new List<string>();
            var substituted = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var card in views)
            {
                projected.Add(AgentCardProjection.Apply(
                    AgentJson.SerializeCard(card), fields, ignored, substituted));
            }

            var available = views.Count > 0
                ? AgentCardProjection.AvailableFields(AgentJson.SerializeCard(views[0]))
                : Array.Empty<string>();

            var projectedPayload = new
            {
                total,
                totalAll,
                totalTemplates,
                returned = projected.Count,
                offset,
                nextOffset,
                truncated = hasMore,
                browserOrganization = browserOrganization?.Name,
                ignoredFields = ignored.Count > 0 ? ignored : null,
                substitutedFields = substituted.Count > 0 ? substituted : null,
                availableFields = ignored.Count > 0 ? available : null,
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
            totalAll,
            totalTemplates,
            returned = views.Count,
            offset,
            nextOffset,
            truncated = hasMore,
            browserOrganization = browserOrganization?.Name,
            views
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Matched " + total + " view(s); returned " + views.Count + "." +
            (total > views.Count
                ? " The result is truncated — narrow the filters or raise the limit."
                : string.Empty));
    }

    private static List<FolderNode> BuildFolderTree(IReadOnlyList<ViewCard> cards)
    {
        var roots = new List<FolderNode>();
        foreach (var card in cards)
        {
            var path = card.BrowserFolderPath;
            if (path is null || path.Count == 0)
            {
                var ungrouped = FindOrAdd(roots, "(no folder)");
                ungrouped.Count++;
                continue;
            }

            var level = roots;
            foreach (var segment in path)
            {
                var node = FindOrAdd(level, segment);
                node.Count++;
                level = node.Children;
            }
        }

        Sort(roots);
        return roots;

        static FolderNode FindOrAdd(List<FolderNode> nodes, string name)
        {
            foreach (var node in nodes)
            {
                if (string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
                    return node;
            }

            var created = new FolderNode { Name = name };
            nodes.Add(created);
            return created;
        }

        static void Sort(List<FolderNode> nodes)
        {
            nodes.Sort((left, right) =>
            {
                var byCount = right.Count.CompareTo(left.Count);
                return byCount != 0
                    ? byCount
                    : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            });

            foreach (var node in nodes)
                Sort(node.Children);
        }
    }

    private sealed class FolderNode
    {
        public string Name { get; set; } = string.Empty;

        public int Count { get; set; }

        public List<FolderNode> Children { get; set; } = new();
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

    internal static bool IsSupportedView(View view)
    {
        return view.ViewType != ViewType.DrawingSheet
               && view.ViewType != ViewType.Internal
               && view.ViewType != ViewType.ProjectBrowser
               && view.ViewType != ViewType.SystemBrowser
               && view.ViewType != ViewType.Undefined;
    }

    internal static bool MatchesType(View view, IReadOnlyList<string> requestedTypes)
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

    internal static bool MatchesLevel(View view, IReadOnlyList<long> levelIds)
    {
        if (levelIds.Count == 0)
            return true;

        var level = view.GenLevel;
        if (level is null)
            return false;

        var id = level.Id.GetValue();
        return levelIds.Contains(id);
    }

    internal static bool MatchesName(View view, string? nameContains)
    {
        if (string.IsNullOrWhiteSpace(nameContains))
            return true;

#if NET8_0_OR_GREATER
        return view.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase);
#else
        return view.Name.IndexOf(nameContains!, StringComparison.OrdinalIgnoreCase) >= 0;
#endif
    }

    internal static bool MatchesFolder(ViewCard card, IReadOnlyList<string> folders)
    {
        if (folders.Count == 0)
            return true;

        if (!card.InBrowser)
            return false;

        var folder = card.BrowserFolder ?? string.Empty;
        foreach (var requested in folders)
        {
            if (string.IsNullOrWhiteSpace(requested))
                continue;

#if NET8_0_OR_GREATER
            if (folder.Contains(requested, StringComparison.OrdinalIgnoreCase))
                return true;
#else
            if (folder.IndexOf(requested, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
#endif
        }

        return false;
    }

    internal static ViewCard CreateCard(
        Document doc,
        View view,
        BrowserOrganization? browserOrganization,
        IReadOnlyDictionary<long, IReadOnlyList<string>>? scheduleSheetNumbers = null)
    {
        var level = view.GenLevel;
        var templateId = view.ViewTemplateId;
        var hasTemplate = templateId != ElementId.InvalidElementId;
        var template = hasTemplate ? doc.GetElement(templateId) : null;
        var sheetNumber = view.get_Parameter(BuiltInParameter.VIEWER_SHEET_NUMBER)?.AsString();
        if (sheetNumber is "—" or "-" or "---" or "")
            sheetNumber = null;

        if (sheetNumber is null
            && view is ViewSchedule
            && scheduleSheetNumbers is not null
            && scheduleSheetNumbers.TryGetValue(view.Id.GetValue(), out var placedOn)
            && placedOn.Count > 0)
        {
            sheetNumber = string.Join(", ", placedOn);
        }

        var (browserFolder, inBrowser, folderPath, folderParameters) =
            ReadBrowserFolder(doc, browserOrganization, view);

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
            SheetNumber = sheetNumber,
            BrowserFolder = browserFolder,
            BrowserFolderPath = folderPath,
            BrowserFolderParameters = folderParameters,
            InBrowser = inBrowser,
            Section = AgentViewSection.ReadValue(view)
        };
    }

    private static (
        string? Folder,
        bool InBrowser,
        IReadOnlyList<string> Path,
        IReadOnlyList<ViewBrowserFolderSegment> Parameters) ReadBrowserFolder(
        Document doc,
        BrowserOrganization? organization,
        View view)
    {
        if (organization is null)
            return (null, true, Array.Empty<string>(), Array.Empty<ViewBrowserFolderSegment>());

        try
        {
            if (!organization.AreFiltersSatisfied(view.Id))
            {
                return (null, false, Array.Empty<string>(),
                    Array.Empty<ViewBrowserFolderSegment>());
            }

            var items = organization.GetFolderItems(view.Id);
            if (items is null || items.Count == 0)
            {
                return (string.Empty, true, Array.Empty<string>(),
                    Array.Empty<ViewBrowserFolderSegment>());
            }

            var parts = new List<string>(items.Count);
            var segments = new List<ViewBrowserFolderSegment>(items.Count);
            foreach (var item in items)
            {
                var parameterId = GetFolderParameterId(item);
                var parameter = DescribeParameter(doc, parameterId);

                var name = item.Name;
                if (IsUnresolvedFolderName(name))
                    name = parameter.Name;

                if (IsUnresolvedFolderName(name))
                    continue;

                parts.Add(name!);
                segments.Add(new ViewBrowserFolderSegment
                {
                    Segment = name!,
                    ParameterId = parameterId?.GetValue(),
                    ParameterName = parameter.Name,
                    IsShared = parameter.IsShared,
                    IsBuiltIn = parameter.IsBuiltIn
                });
            }

            return (string.Join(" / ", parts), true, parts, segments);
        }
        catch (Exception)
        {
            return (null, true, Array.Empty<string>(), Array.Empty<ViewBrowserFolderSegment>());
        }
    }

    private static ElementId? GetFolderParameterId(FolderItemInfo item)
    {
#if REVIT2026_OR_GREATER
        var path = item.GetGroupingParameterIdPath();
        return path is null || path.Count == 0 ? null : path[path.Count - 1];
#else
        return item.ElementId;
#endif
    }

    private static bool IsUnresolvedFolderName(string? name) =>
        string.IsNullOrWhiteSpace(name)
        || name is "???" or "?" or "??? ";

    private static (string? Name, bool IsShared, bool IsBuiltIn) DescribeParameter(
        Document doc,
        ElementId? parameterId)
    {
        if (parameterId is null || parameterId == ElementId.InvalidElementId)
            return (null, false, false);

        try
        {
            var value = parameterId.GetValue();
            if (value < 0)
            {
                try
                {
                    return (LabelUtils.GetLabelFor((BuiltInParameter)value), false, true);
                }
                catch (Exception)
                {
                    return (null, false, true);
                }
            }

            var element = doc.GetElement(parameterId);
            if (element is SharedParameterElement shared)
                return (shared.Name, true, false);

            if (element is ParameterElement parameter)
                return (parameter.Name, false, false);

            return (element?.Name, false, false);
        }
        catch (Exception)
        {
            return (null, false, false);
        }
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
