using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Validation half of build_system_sheets: per-item pre-flight (placeable view,
/// free sheet number, resolvable schedule and title block, the explication
/// block, album inheritance lookup) plus the request/plan DTOs the validated
/// items are carried in.
/// </summary>
public sealed partial class BuildSystemSheetsTool
{
    private static PlannedItem? ValidateItem(
        Document doc,
        WriteOutcome outcome,
        BuildSheetItem item,
        WriteItemReport report,
        Dictionary<long, string> viewportOwners,
        ElementId? defaultTitleBlockId,
        string numberPrefix,
        ref int nextValue,
        int numberDigits,
        HashSet<string> usedNumbers,
        HashSet<string> usedScheduleNames,
        Dictionary<long, Dictionary<string, string>?> inheritedCache)
    {
        if (doc.GetElement(ElementIdCompat.Create(item.ViewId)) is not View view)
        {
            Fail(outcome, report, "view_not_found",
                "Element " + item.ViewId + " does not exist in the active document.");
            return null;
        }

        report.Name = view.Name;

        if (view.IsTemplate || view is ViewSchedule or ViewSheet)
        {
            Fail(outcome, report, "view_not_placeable",
                "View '" + view.Name + "' (" + view.ViewType + ") cannot be placed with " +
                "Viewport.Create. Templates, schedules and sheets are not placeable.");
            return null;
        }

        if (view.ViewType != ViewType.Legend
            && viewportOwners.TryGetValue(item.ViewId, out var ownerNumber))
        {
            Fail(outcome, report, "view_already_placed",
                "View '" + view.Name + "' is already placed on sheet '" + ownerNumber +
                "'. Duplicate the view first to place it on a new sheet.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(item.SheetName))
        {
            Fail(outcome, report, "invalid_arguments",
                "sheetName is required for every item.");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(item.ViewportCorner)
            && !string.Equals(item.ViewportCorner, "top-left", StringComparison.OrdinalIgnoreCase))
        {
            Fail(outcome, report, "invalid_arguments",
                "viewportCorner '" + item.ViewportCorner + "' is not supported; only " +
                "'top-left' is available.");
            return null;
        }

        var marginMm = item.MarginMm ?? DefaultMarginMm;
        if (marginMm < 0)
        {
            Fail(outcome, report, "invalid_arguments", "marginMm must not be negative.");
            return null;
        }

        ViewSchedule? schedule = null;
        if (item.ScheduleId is > 0)
        {
            if (doc.GetElement(ElementIdCompat.Create(item.ScheduleId.Value)) is not ViewSchedule found)
            {
                Fail(outcome, report, "schedule_not_found",
                    "Element " + item.ScheduleId + " is not a schedule.");
                return null;
            }

            if (found.IsTemplate)
            {
                Fail(outcome, report, "schedule_is_template",
                    "Schedule '" + found.Name + "' is a template and cannot be placed.");
                return null;
            }

            schedule = found;
        }

        ElementId titleBlockId;
        if (item.TitleBlockTypeId is > 0)
        {
            var resolved = AgentSheetWriteHelper.FindTitleBlockType(
                doc, item.TitleBlockTypeId.Value, out _, out var typeMessage);
            if (resolved is null)
            {
                Fail(outcome, report, "titleblock_not_found", typeMessage!);
                return null;
            }

            titleBlockId = resolved;
        }
        else
        {
            titleBlockId = defaultTitleBlockId!;
        }

        string number;
        if (!string.IsNullOrWhiteSpace(item.SheetNumber))
        {
            number = item.SheetNumber!.Trim();
            var invalidReason = AgentSheetWriteHelper.ValidateNumber(number);
            if (invalidReason is not null)
            {
                Fail(outcome, report, "sheet_number_invalid", invalidReason);
                return null;
            }
        }
        else
        {
            number = AgentSheetReader.FormatContinuedNumber(numberPrefix, nextValue, numberDigits);
            nextValue++;
        }

        if (usedNumbers.Contains(number))
        {
            Fail(outcome, report, "sheet_number_taken",
                "Number '" + number + "' is used by another item of this batch.");
            return null;
        }

        var owner = AgentSheetWriteHelper.FindNumberOwner(doc, number, 0);
        if (owner is not null)
        {
            Fail(outcome, report, "sheet_number_taken",
                "Sheet number '" + number + "' is already used by sheet " +
                AgentSheetWriteHelper.SheetLabel(owner) + " (id " + owner.Id.GetValue() + ").");
            return null;
        }

        usedNumbers.Add(number);

        // C6-11: without an explicit value the album's common title block
        // parameters (e.g. the size multiplicity) are inherited, not left 0.
        if (!inheritedCache.TryGetValue(titleBlockId.GetValue(), out var inherited))
        {
            inherited = InheritedTitleBlockValues(doc, titleBlockId);
            inheritedCache.Add(titleBlockId.GetValue(), inherited);
        }

        var explication = ValidateExplication(
            doc, outcome, item, report, number, usedScheduleNames);
        if (explication is null && item.Explication is not null)
            return null;

        return new PlannedItem
        {
            View = view,
            Schedule = schedule,
            Number = number,
            SheetName = item.SheetName!.Trim(),
            TitleBlockTypeId = titleBlockId,
            MarginMm = marginMm,
            TitleBlockParameters = item.TitleBlockParameters,
            InheritedTitleBlockValues = inherited,
            Explication = explication
        };
    }

    private static void Fail(
        WriteOutcome outcome,
        WriteItemReport report,
        string errorCode,
        string reason)
    {
        report.ErrorCode = errorCode;
        report.Reason = reason;
        outcome.Batch.Failed.Add(report);
    }

    private sealed class BuildSheetsRequest
    {
        public List<BuildSheetItem>? Items { get; set; }
    }

    private sealed class BuildSheetItem
    {
        public long ViewId { get; set; }

        public long? ScheduleId { get; set; }

        public ExplicationRequest? Explication { get; set; }

        public string? SheetName { get; set; }

        public string? SheetNumber { get; set; }

        public long? TitleBlockTypeId { get; set; }

        public Dictionary<string, JsonElement>? TitleBlockParameters { get; set; }

        public string? ViewportCorner { get; set; }

        public double? MarginMm { get; set; }
    }

    private sealed class PlannedItem
    {
        public required View View { get; init; }

        public ViewSchedule? Schedule { get; init; }

        public PlannedExplication? Explication { get; init; }

        public required string Number { get; init; }

        public required string SheetName { get; init; }

        public required ElementId TitleBlockTypeId { get; init; }

        public double MarginMm { get; init; }

        public Dictionary<string, JsonElement>? TitleBlockParameters { get; init; }

        /// <summary>Album-inherited title block values (C6-11), null = no donor sheet.</summary>
        public Dictionary<string, string>? InheritedTitleBlockValues { get; init; }
    }
}
