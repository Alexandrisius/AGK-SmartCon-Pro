using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Sets the table style of a schedule: striped rows (Revit 2021+) and the
/// default text types of the body, the column headers and the title. The whole
/// style snapshot is recorded for undo.
/// </summary>
public sealed class SetScheduleStyleTool : AgentWriteToolBase
{
    public SetScheduleStyleTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_schedule_style",
        Description =
            "Sets the table style of one schedule by scheduleId or exact scheduleName: " +
            "stripedRows (zebra rows), useStripedRowsOnSheets (zebra rows also on sheets; " +
            "both Revit 2021+ only) and bodyTextTypeId/headerTextTypeId/titleTextTypeId — " +
            "the default text types (TextStyle element ids; get_schedule_definition reports " +
            "the current ids and their names). Values equal to the current ones are skipped " +
            "(no-op); an invalid text type id fails with text_type_not_valid. undo_last " +
            "restores the previous style. Dry run defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""},""stripedRows"":{""type"":""boolean""},""useStripedRowsOnSheets"":{""type"":""boolean""},""bodyTextTypeId"":{""type"":""integer""},""headerTextTypeId"":{""type"":""integer""},""titleTextTypeId"":{""type"":""integer""},""dryRun"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var hasStriped = args.TryGetProperty("stripedRows", out var stripedElement)
                         && stripedElement.ValueKind is JsonValueKind.True or JsonValueKind.False;
        var hasSheetStriped = args.TryGetProperty("useStripedRowsOnSheets", out var sheetStripedElement)
                              && sheetStripedElement.ValueKind is JsonValueKind.True or JsonValueKind.False;
        var hasBody = args.TryGetProperty("bodyTextTypeId", out _);
        var hasHeader = args.TryGetProperty("headerTextTypeId", out _);
        var hasTitle = args.TryGetProperty("titleTextTypeId", out _);

        if (!hasStriped && !hasSheetStriped && !hasBody && !hasHeader && !hasTitle)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass at least one of stripedRows, useStripedRowsOnSheets, " +
                              "bodyTextTypeId, headerTextTypeId, titleTextTypeId.";
            return;
        }

        if (!AgentScheduleWriteHelper.TryResolveSchedule(
                doc, args, out var schedule, out var errorCode, out var message))
        {
            outcome.Success = false;
            outcome.ErrorCode = errorCode;
            outcome.Message = message;
            return;
        }

        if ((hasStriped || hasSheetStriped) && !AgentScheduleWriteHelper.SupportsStripedRows)
        {
            outcome.Success = false;
            outcome.ErrorCode = "style_not_supported_on_version";
            outcome.Message = "Striped rows are available starting from Revit 2021; this " +
                              "Revit version does not expose them. Set the text types instead.";
            return;
        }

        var view = schedule!;
        var oldSnapshot = AgentScheduleWriteHelper.SerializeStyle(view);
        outcome.Description = "Set style of schedule " + view.Name;
        var changed = 0;

        if (hasStriped
            && !ApplyStriped(view, stripedElement.ValueKind == JsonValueKind.True,
                sheet: false, outcome, ref changed))
            return;

        if (hasSheetStriped
            && !ApplyStriped(view, sheetStripedElement.ValueKind == JsonValueKind.True,
                sheet: true, outcome, ref changed))
            return;

        if (!ApplyTextType(args, view, hasBody, "bodyTextTypeId",
                id => view.BodyTextTypeId = id, outcome, ref changed)
            || !ApplyTextType(args, view, hasHeader, "headerTextTypeId",
                id => view.HeaderTextTypeId = id, outcome, ref changed)
            || !ApplyTextType(args, view, hasTitle, "titleTextTypeId",
                id => view.TitleTextTypeId = id, outcome, ref changed))
            return;

        if (changed == 0)
        {
            outcome.SuppressChangeSet = true;
            outcome.Data["scheduleId"] = view.Id.GetValue();
            outcome.Data["style"] = ReadBackStyle(doc, view);
            outcome.Message = "The schedule already has this style (no-op).";
            return;
        }

        outcome.AffectedCount = changed;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = view.UniqueId,
            ElementId = view.Id.GetValue(),
            ParameterName = "schedule_style",
            OldValue = oldSnapshot,
            NewValue = AgentScheduleWriteHelper.SerializeStyle(view),
            Status = ChangeStatus.Applied
        });
        outcome.Data["scheduleId"] = view.Id.GetValue();
        outcome.Data["style"] = ReadBackStyle(doc, view);
    }

    private static bool ApplyStriped(
        ViewSchedule schedule,
        bool requested,
        bool sheet,
        WriteOutcome outcome,
        ref int changed)
    {
        try
        {
#if REVIT2021_OR_GREATER
            var current = sheet ? schedule.UseStripedRowsOnSheets : schedule.HasStripedRows;
            if (current == requested)
                return true;

            if (sheet)
                schedule.UseStripedRowsOnSheets = requested;
            else
                schedule.HasStripedRows = requested;

            changed++;
            var key = sheet ? "useStripedRowsOnSheets" : "stripedRows";
            outcome.Batch.Changed.Add(new WriteItemReport
            {
                Id = schedule.Id.GetValue(),
                Name = schedule.Name,
                OldValue = key + "=" + current,
                NewValue = key + "=" + requested
            });
            return true;
#else
            outcome.Success = false;
            outcome.ErrorCode = "style_not_supported_on_version";
            outcome.Message = "Striped rows need Revit 2021 or newer.";
            return false;
#endif
        }
        catch (Exception ex)
        {
            outcome.Success = false;
            outcome.ErrorCode = "set_failed";
            outcome.Message = "Revit refused the striped rows flag: " +
                              ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static bool ApplyTextType(
        JsonElement args,
        ViewSchedule schedule,
        bool requested,
        string key,
        Action<ElementId> assign,
        WriteOutcome outcome,
        ref int changed)
    {
        if (!requested)
            return true;

        var typeId = AgentJson.GetLong(args, key, 0);
        if (typeId <= 0)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = key + " must be a positive element id of a text type.";
            return false;
        }

        var elementId = ElementIdCompat.Create(typeId);
        bool valid;
        try
        {
            valid = schedule.IsValidTextTypeId(elementId);
        }
        catch (Exception)
        {
            valid = false;
        }

        if (!valid)
        {
            outcome.Success = false;
            outcome.ErrorCode = "text_type_not_valid";
            outcome.Message = "Element " + typeId + " is not a valid text type for " +
                              "schedule properties (see the TextStyle types of the document).";
            return false;
        }

        try
        {
            var current = CurrentTextTypeId(schedule, key);
            if (current == typeId)
                return true;

            assign(elementId);
            changed++;
            outcome.Batch.Changed.Add(new WriteItemReport
            {
                Id = schedule.Id.GetValue(),
                Name = schedule.Name,
                OldValue = key + "=" + current,
                NewValue = key + "=" + typeId
            });
            return true;
        }
        catch (Exception ex)
        {
            outcome.Success = false;
            outcome.ErrorCode = "set_failed";
            outcome.Message = "Revit refused " + key + ": " + ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static long CurrentTextTypeId(ViewSchedule schedule, string key) => key switch
    {
        "bodyTextTypeId" => TextTypeId(schedule.BodyTextTypeId),
        "headerTextTypeId" => TextTypeId(schedule.HeaderTextTypeId),
        "titleTextTypeId" => TextTypeId(schedule.TitleTextTypeId),
        _ => 0
    };

    private static long TextTypeId(ElementId id) =>
        id is null || id == ElementId.InvalidElementId ? 0 : id.GetValue();

    internal static object ReadBackStyle(Document doc, ViewSchedule schedule)
    {
        bool? striped = null;
        bool? sheetStriped = null;
#if REVIT2021_OR_GREATER
        try
        {
            striped = schedule.HasStripedRows;
            sheetStriped = schedule.UseStripedRowsOnSheets;
        }
        catch (Exception)
        {
            // Striped rows stay null when unreadable.
        }
#endif

        return new
        {
            stripedRows = striped,
            useStripedRowsOnSheets = sheetStriped,
            bodyTextTypeId = TextTypeId(schedule.BodyTextTypeId),
            headerTextTypeId = TextTypeId(schedule.HeaderTextTypeId),
            titleTextTypeId = TextTypeId(schedule.TitleTextTypeId),
            bodyTextTypeName = TextTypeName(doc, schedule.BodyTextTypeId),
            headerTextTypeName = TextTypeName(doc, schedule.HeaderTextTypeId),
            titleTextTypeName = TextTypeName(doc, schedule.TitleTextTypeId)
        };
    }

    private static string? TextTypeName(Document doc, ElementId id) =>
        id is null || id == ElementId.InvalidElementId ? null : doc.GetElement(id)?.Name;
}
