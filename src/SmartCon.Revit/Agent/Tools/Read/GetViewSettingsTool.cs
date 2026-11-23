using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class GetViewSettingsTool : AgentReadToolBase
{
    private const int MaxBatchViews = 200;

    private const string CompactHint =
        "Compact summary. Pass fields=[\"scale\",\"viewRange\",\"crop\",\"hiddenCategories\",...] " +
        "for selected settings or compact=false for the full payload. The hidden-category list " +
        "is omitted from the full payload unless expandHiddenCategories=true; " +
        "hiddenCategoryCount is always present. hiddenCategoryEditableCount shows how many of " +
        "them Revit still allows to toggle on this view; the rest are hidden but not " +
        "editable here (template-controlled V/G or a view-type limitation).";

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
            "Returns the current settings of one view or of a batch: view template, scale, detail " +
            "level, discipline, plan view range (levels and offsets in mm), crop region box (mm), " +
            "hidden categories and hidden worksets. Pass viewIds to read many views in one call " +
            "(audits of 5–50 views do not need a round trip each). Without fields and with " +
            "compact=true (default) it returns a compact summary (template, scale, counts); pass " +
            "fields to request specific settings or compact=false for the full payload. " +
            "hiddenCategories is omitted from the full payload unless expandHiddenCategories=true " +
            "(the count is always present); a fields request for hiddenCategories expands it. " +
            "hiddenCategoryCount is the total the view shows hidden, hiddenCategoryEditableCount " +
            "is the part Revit still allows to toggle here (the rest is hidden but not " +
            "editable: template-controlled V/G or a view-type limitation).",
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson + @"""viewId"":{""type"":""integer""},""viewIds"":{""type"":""array"",""description"":""Batch read: up to 200 view ids; returns views[]. In fields mode every row still carries viewId so rows are identifiable"",""items"":{""type"":""integer""}},""fields"":{""type"":""array"",""description"":""Allowed: viewId,name,viewType,templateId,templateName,hasTemplate,scale,detailLevel,discipline,viewRange,crop,hiddenCategories,hiddenCategoryCount,hiddenCategoryEditableCount,hiddenWorksets,controlledParameters,nonControlledParameters. Aliases like template resolve to templateName."",""items"":{""type"":""string""}},""expandHiddenCategories"":{""type"":""boolean"",""default"":false},""compact"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var viewIds = AgentJson.GetLongArray(args, "viewIds");
        if (viewIds.Count > 0)
            return ExecuteBatch(doc, viewIds, args, ct);

        var viewId = AgentJson.GetLong(args, "viewId", 0);
        if (viewId == 0)
            return ToolResult.Fail("invalid_arguments", "viewId or viewIds is required.");

        if (doc.GetElement(ElementIdCompat.Create(viewId)) is not View view)
        {
            return ToolResult.Fail(
                "view_not_found",
                "No view with id " + viewId + " exists in this document.");
        }

        var settings = AgentViewSettingsReader.Read(doc, view);
        var fields = AgentJson.GetStringArray(args, "fields");
        var expandHiddenCategories = AgentJson.GetBool(args, "expandHiddenCategories", false)
                                    || fields.Any(field => string.Equals(
                                        field,
                                        "hiddenCategories",
                                        StringComparison.OrdinalIgnoreCase));

        if (fields.Count > 0)
        {
            var payload = Project(AgentJson.SerializeCard(settings), fields);
            return ToolResult.Ok(
                payload,
                "Read the requested settings of view '" + view.Name + "'.");
        }

        if (!AgentJson.GetBool(args, "compact", true))
        {
            var full = AgentJson.Serialize(settings);
            if (!expandHiddenCategories)
                full = OmitHiddenCategories(full);

            return ToolResult.Ok(
                full,
                "Read the full settings of view '" + view.Name + "'." +
                (expandHiddenCategories
                    ? string.Empty
                    : " Hidden categories are summarized by hiddenCategoryCount; pass " +
                      "expandHiddenCategories=true to include the list."));
        }

        var summary = AgentViewSettingsReader.CreateSummary(settings);
        return ToolResult.Ok(
            Compact(doc, view, settings, summary),
            "Read a compact summary of view '" + view.Name + "'. " + CompactHint);
    }

    /// <summary>
    /// Batch read: the answer to "which template is on each of these five plans"
    /// used to cost one call per view (external benchmark, task 1).
    /// </summary>
    private ToolResult ExecuteBatch(
        Document doc,
        IReadOnlyList<long> requestedIds,
        JsonElement args,
        CancellationToken ct)
    {
        if (requestedIds.Count > MaxBatchViews)
        {
            return ToolResult.Fail(
                "limit_exceeded",
                "At most " + MaxBatchViews + " view ids per call are allowed; split the list.");
        }

        var fields = AgentJson.GetStringArray(args, "fields");
        var compact = AgentJson.GetBool(args, "compact", true);
        var expandHiddenCategories = AgentJson.GetBool(args, "expandHiddenCategories", false)
                                    || fields.Any(field => string.Equals(
                                        field,
                                        "hiddenCategories",
                                        StringComparison.OrdinalIgnoreCase));

        var views = new List<JsonNode?>(requestedIds.Count);
        var notFound = new List<long>();
        var ignored = new List<string>();
        var substituted = new Dictionary<string, string>(StringComparer.Ordinal);
        var templateControlsCache = new Dictionary<long, JsonObject>();
        JsonElement? firstSettingsJson = null;

        foreach (var viewId in requestedIds)
        {
            ct.ThrowIfCancellationRequested();

            if (doc.GetElement(ElementIdCompat.Create(viewId)) is not View view)
            {
                notFound.Add(viewId);
                continue;
            }

            var settings = AgentViewSettingsReader.Read(doc, view);
            if (fields.Count > 0)
            {
                var serialized = AgentJson.SerializeCard(settings);
                firstSettingsJson ??= serialized;
                var projected = Project(
                    serialized, fields, ignored, substituted, withDiagnostics: false);
                var node = JsonNode.Parse(projected.GetRawText()) as JsonObject ?? new JsonObject();
                node["viewId"] = settings.ViewId;
                views.Add(node);
                continue;
            }

            if (!compact)
            {
                var full = AgentJson.Serialize(settings);
                if (!expandHiddenCategories)
                    full = OmitHiddenCategories(full);
                views.Add(JsonNode.Parse(full.GetRawText()));
                continue;
            }

            var summary = AgentViewSettingsReader.CreateSummary(settings);
            views.Add(CompactNode(
                doc, view, settings, summary,
                includeTemplateParameterList: false,
                templateControlsCache));
        }

        if (views.Count == 0 && notFound.Count > 0)
        {
            return ToolResult.Fail(
                "view_not_found",
                "None of the requested view ids exist in this document.");
        }

        var payload = new JsonObject
        {
            ["mode"] = "batch",
            ["total"] = requestedIds.Count,
            ["returned"] = views.Count,
            ["notFound"] = notFound.Count == 0 ? null : ToArray(notFound),
            ["views"] = ToArray(views),
            ["hint"] = CompactHint
        };
        if (ignored.Count > 0)
        {
            payload["ignoredFields"] = ToArray(ignored);
            if (firstSettingsJson is { } settingsJson)
            {
                payload["availableFields"] = ToArray(
                    AgentCardProjection.AvailableFields(settingsJson));
            }
        }

        if (substituted.Count > 0)
            payload["substitutedFields"] = JsonSerializer.SerializeToNode(substituted);

        return ToolResult.Ok(
            JsonSerializer.SerializeToElement(payload),
            "Read settings of " + views.Count + " view(s)." +
            (notFound.Count > 0
                ? " " + notFound.Count + " id(s) not found or not a view."
                : string.Empty));
    }

    private static JsonElement Project(
        JsonElement full,
        IReadOnlyList<string> fields,
        List<string>? ignored = null,
        Dictionary<string, string>? substituted = null,
        bool withDiagnostics = true)
    {
        ignored ??= new List<string>();
        substituted ??= new Dictionary<string, string>(StringComparer.Ordinal);
        var projected = AgentCardProjection.Apply(full, fields, ignored, substituted);
        if (!withDiagnostics || (ignored.Count == 0 && substituted.Count == 0))
            return projected;

        var node = JsonNode.Parse(projected.GetRawText()) as JsonObject ?? new JsonObject();
        if (ignored.Count > 0)
        {
            node["ignoredFields"] = ToArray(ignored);
            node["availableFields"] = ToArray(AgentCardProjection.AvailableFields(full));
        }

        if (substituted.Count > 0)
            node["substitutedFields"] = JsonSerializer.SerializeToNode(substituted);

        return JsonSerializer.SerializeToElement(node);
    }

    private static JsonElement OmitHiddenCategories(JsonElement full)
    {
        if (full.ValueKind != JsonValueKind.Object)
            return full;

        var node = JsonNode.Parse(full.GetRawText()) as JsonObject;
        if (node is null)
            return full;

        node.Remove("hiddenCategories");
        node["hiddenCategoriesOmitted"] = true;
        return JsonSerializer.SerializeToElement(node);
    }

    private static JsonElement Compact(
        Document doc,
        View view,
        ViewSettings settings,
        ViewSettingsSummary summary) =>
        JsonSerializer.SerializeToElement(
            CompactNode(doc, view, settings, summary, includeTemplateParameterList: true));

    private static JsonObject CompactNode(
        Document doc,
        View view,
        ViewSettings settings,
        ViewSettingsSummary summary,
        bool includeTemplateParameterList,
        Dictionary<long, JsonObject>? templateControlsCache = null)
    {
        var node = JsonNode.Parse(AgentJson.Serialize(summary).GetRawText()) as JsonObject
                   ?? new JsonObject();

        // "Where can I write?" is the first question after a view_controlled_by_template
        // error. Report it instead of making the model guess (external run, B2).
        node["writable"] = WritableMap(view);

        // Why a view parameter is read-only: the template controls it. The compact
        // summary used to hide that, which cost the model several diagnostic rounds
        // (external benchmark, tasks 1 and 3).
        if (settings.TemplateId is > 0)
        {
            if (templateControlsCache is not null
                && templateControlsCache.TryGetValue(settings.TemplateId.Value, out var cached))
            {
                node["templateControls"] = cached.DeepClone();
                return node;
            }

            if (doc.GetElement(ElementIdCompat.Create(settings.TemplateId.Value)) is View template)
            {
                var templateSettings = AgentViewSettingsReader.Read(doc, template);
                var controls = new JsonObject
                {
                    ["templateId"] = settings.TemplateId.Value,
                    ["templateName"] = template.Name,
                    ["controlledParameterCount"] = templateSettings.ControlledParameters.Count,
                    ["viewRange"] = AgentViewSettingsReader.IsTemplateParameterControlled(
                        template, BuiltInParameter.PLAN_VIEW_RANGE),
                    ["crop"] = AgentViewSettingsReader.IsTemplateParameterControlled(
                        template, BuiltInParameter.VIEWER_CROP_REGION),
                    ["visibility"] = AgentViewSettingsReader.IsTemplateParameterControlled(
                        template, BuiltInParameter.VIS_GRAPHICS_MODEL),
                    ["filters"] = AgentViewSettingsReader.IsTemplateParameterControlled(
                        template, BuiltInParameter.VIS_GRAPHICS_FILTERS)
                };

                // In a batch the parameter list repeats once per view and dominates the
                // payload; the single-view answer keeps it.
                if (includeTemplateParameterList)
                {
                    var parameters = new JsonArray();
                    foreach (var name in templateSettings.ControlledParameters.Take(50))
                        parameters.Add(name);
                    controls["controlledParameters"] = parameters;
                }

                node["templateControls"] = controls;
                if (templateControlsCache is not null
                    && !templateControlsCache.ContainsKey(settings.TemplateId.Value))
                {
                    templateControlsCache[settings.TemplateId.Value] = controls;
                }
            }
        }

        return node;
    }

    /// <summary>
    /// Which settings the view itself can still write. False means the V/G, crop,
    /// view-range or filter parameter comes from the view template; the template id
    /// is in templateControls.
    /// </summary>
    private static JsonObject WritableMap(View view)
    {
        return new JsonObject
        {
            // false = the view template controls this parameter. The view type may
            // forbid a write even when this is true (schedule/legend/3D), so the
            // capability is reported separately below.
            ["visibility"] = CanWrite(view, BuiltInParameter.VIS_GRAPHICS_MODEL),
            ["crop"] = CanWrite(view, BuiltInParameter.VIEWER_CROP_REGION),
            ["viewRange"] = CanWrite(view, BuiltInParameter.PLAN_VIEW_RANGE),
            ["filters"] = CanWrite(view, BuiltInParameter.VIS_GRAPHICS_FILTERS),
            ["filtersSupported"] = AgentFilterUsage.SupportsFilters(view),
            ["viewRangeApplicable"] = view is ViewPlan
        };
    }

    private static bool CanWrite(View view, BuiltInParameter parameter) =>
        !AgentViewSettingsReader.IsParameterLockedByTemplate(view, parameter, out _, out _);

    private static JsonArray ToArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }

    private static JsonArray ToArray(IReadOnlyList<long> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }

    private static JsonArray ToArray(IEnumerable<JsonNode?> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value?.DeepClone());
        return array;
    }
}