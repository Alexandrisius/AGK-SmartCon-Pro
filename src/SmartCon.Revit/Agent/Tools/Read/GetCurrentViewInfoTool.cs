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

    public override bool SupportsDocumentRouting => false;

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
                "No view is active in this session. Read a view by id with " +
                "get_view_settings(viewId) or list the views with list_views.");
        }

        var templateId = view.ViewTemplateId;
        var hasTemplate = templateId != ElementId.InvalidElementId;
        var template = hasTemplate ? doc.GetElement(templateId) : null;
        var level = SafeClass(() => view.GenLevel);
        var schedule = view as ViewSchedule;

        var payload = new
        {
            viewId = view.Id.GetValue(),
            uniqueId = view.UniqueId,
            name = view.Name,
            viewType = view.ViewType.ToString(),
            levelId = level is null ? (long?)null : level.Id.GetValue(),
            levelName = level?.Name,
            scale = SafeInt(() => view.Scale),
            detailLevel = SafeText(() => view.DetailLevel.ToString()),
            discipline = SafeText(() => view.Discipline.ToString()),
            isTemplate = view.IsTemplate,
            templateId = hasTemplate ? templateId.GetValue() : (long?)null,
            templateName = template?.Name,
            // Schedules and legends have no crop box: the property throws there.
            cropActive = SafeBool(() => view.CropBoxActive),
            cropVisible = SafeBool(() => view.CropBoxVisible),
            schedule = schedule is null ? null : DescribeSchedule(schedule)
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Active view is '" + view.Name + "' (" + view.ViewType + ")" +
            (schedule is not null
                ? " — a schedule, not a graphical view; its rows' element ids come from " +
                  "get_schedule_elements."
                : string.Empty) + ".");
    }

    /// <summary>
    /// The schedule block of the payload. A schedule is a view without a crop box,
    /// so the regular view properties are read defensively; this part is what the
    /// agent uses to reach the rows' element ids.
    /// </summary>
    internal static Dictionary<string, object?> DescribeSchedule(ViewSchedule schedule)
    {
        return new Dictionary<string, object?>
        {
            ["scheduleId"] = schedule.Id.GetValue(),
            ["isMulticategory"] = SafeBool(() => schedule.Definition is null
                                                || schedule.Definition.CategoryId is null
                                                || schedule.Definition.CategoryId ==
                                                ElementId.InvalidElementId),
            ["rowCount"] = SafeInt(() => schedule.GetTableData()
                .GetSectionData(SectionType.Body)
                .NumberOfRows),
            ["hint"] = "This view is a schedule: the element ids behind its rows come from " +
                       "get_schedule_elements(scheduleId)."
        };
    }

    private static T? SafeClass<T>(Func<T> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? SafeInt(Func<int> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool? SafeBool(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? SafeText(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
