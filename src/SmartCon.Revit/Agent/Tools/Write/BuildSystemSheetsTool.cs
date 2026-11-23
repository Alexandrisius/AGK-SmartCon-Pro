using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Query;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Batch sheet assembly for per-system documentation: per item a new sheet,
/// the view's viewport snapped to the top-left corner INSIDE the sheet border
/// (create → regenerate → measure the box → recenter → verify against the
/// sheet outline; overflow is a warning, never a rollback) and an optional
/// schedule placed to the right of the view. One call = one transaction =
/// one change set; undo_last removes the whole batch.
/// </summary>
public sealed partial class BuildSystemSheetsTool : AgentWriteToolBase
{
    private const int MaxItems = 20;
    private const double DefaultMarginMm = 10;

    public BuildSystemSheetsTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "build_system_sheets",
        Description =
            "Assembles sheets in one batch (up to 20 items; one transaction, one change set). " +
            "Per item: a new sheet (sheetNumber or the album continuation — the same next " +
            "number get_titleblock_options hints), the view placed with its viewport box in " +
            "the TOP-LEFT corner inside the sheet border (marginMm, default 10), and either " +
            "an optional scheduleId placed to the right of the view at the view's top edge " +
            "(a ready clone — this tool does not clone it) or an optional explication block " +
            "built in place: a narrow clone of sourceScheduleId with only the keepFields " +
            "columns visible (a missing column is a warning + skip), an equals filter " +
            "filterField=filterValue (the shared parameter that carries the system name = the system name; the filter " +
            "field is added as a HIDDEN column when it is not a kept one), the clone named " +
            "'Экспликация <sheetName>' and placed to the right of the view. explicationRows " +
            "reports the clone's data rows (dry run included); a filter value that matches no " +
            "source row is a warning naming the closest existing values. scheduleId and " +
            "explication together are invalid_arguments. Explication sources: one " +
            "sourceScheduleId clones that category; sourceScheduleIds (several) builds a " +
            "MULTICATEGORY schedule that covers every category of the system, so pipes, " +
            "fittings, flexible pipes and accessories land in ONE table — its keepFields " +
            "and filterField must then be shared parameters common to those categories " +
            "(a built-in that exists only on MEP categories cannot be a multicategory " +
            "field). The viewport box is unknown before placement (especially for 3D views), " +
            "so the tool places at a provisional point, regenerates, measures GetBoxOutline, " +
            "recenters with SetBoxCenter and verifies the box against ViewSheet.Outline " +
            "(tolerance 1 mm): overflow is reported as a per-item warning with overflow=true " +
            "and NEVER rolls the batch back. Coordinates are in the SHEET FRAME (title-block / " +
            "plot origin, often offset, so values can be negative): sheetOutlineMm in every " +
            "built row names the real sheet rectangle in that same frame — measure offsets " +
            "from it, not from zero. Every built row also carries hiddenCategories and " +
            "visibleCategories (categories with visible elements in the view): the hidden " +
            "list alone never proved that the picture has no non-MEP categories. " +
            "from it, not from zero. titleBlockParameters writes instance parameters " +
            "of the new sheet's title block — e.g. a size multiplicity switching the paper " +
            "format (find editable names via get_titleblock_options); numeric values for " +
            "double parameters are written as raw internal values. Parameters NOT passed " +
            "explicitly are inherited from the album: every editable non-built-in instance " +
            "parameter that holds the same value on ALL existing sheets of the same title " +
            "block type is copied to the new sheet (status \"inherited\" in the result), so " +
            "the size multiplicity keeps the album value instead of resetting to 0; a value " +
            "the explicitly passed parameters have just recomputed (Формат А → Ширина/Высота) " +
            "is reported as \"kept\" with its current value and NOT overwritten. " +
            "Guards per item: " +
            "view_not_placeable, view_already_placed, sheet_number_taken, " +
            "titleblock_not_found, titleblock_parameter_not_found (a failed parameter is a " +
            "warning — the sheet stays); for explication also schedule_not_found, " +
            "schedule_is_template, schedule_not_cloneable, filter_field_not_schedulable, " +
            "schedule_name_taken. atomic=true aborts the whole batch when any item " +
            "fails validation. undo_last deletes the batch (sheet, viewport, the " +
            "explication clone and its placement). Dry run defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""items"":{""type"":""array"",""minItems"":1,""maxItems"":20,""items"":{""type"":""object"",""properties"":{""viewId"":{""type"":""integer"",""description"":""View to place (the system 3D view)""},""scheduleId"":{""type"":""integer"",""description"":""Schedule to place to the right of the view (a ready clone — this tool does not clone)""},""explication"":{""type"":""object"",""description"":""Build the explication right of the view instead of placing a ready scheduleId: a narrow clone of sourceScheduleId — visible keepFields columns only, an equals filter filterField=filterValue, the filter field added as a hidden column when it is not kept. Cannot be combined with scheduleId."",""properties"":{""sourceScheduleId"":{""type"":""integer"",""description"":""The source schedule to clone (see list_schedules); use sourceScheduleIds for a system across several categories""},""sourceScheduleIds"":{""type"":""array"",""maxItems"":15,""items"":{""type"":""integer""},""description"":""Several sources → one MULTICATEGORY explication covering all categories of the system (fields must be shared parameters common to those categories)""},""keepFields"":{""type"":""array"",""minItems"":1,""maxItems"":15,""items"":{""type"":""string""},""description"":""Visible columns of the explication by display name, e.g. Family/Type/Count — copy the column list from the task""},""filterField"":{""type"":""string"",""description"":""Filter field: a schedulable column name (a shared parameter works)""},""filterValue"":{""type"":""string"",""description"":""Filter value: the row value this sheet is about""}},""required"":[""sourceScheduleId"",""keepFields"",""filterField"",""filterValue""],""additionalProperties"":false},""sheetName"":{""type"":""string"",""description"":""Sheet name, short and engineering-style""},""sheetNumber"":{""type"":""string"",""description"":""Unique sheet number; omit to continue the album numbering""},""titleBlockTypeId"":{""type"":""integer"",""description"":""Title block type; default: first loaded""},""titleBlockParameters"":{""type"":""object"",""description"":""Instance parameters of the sheet's title block: {parameter name: value} (e.g. size multiplicity)""},""viewportCorner"":{""type"":""string"",""enum"":[""top-left""],""default"":""top-left""},""marginMm"":{""type"":""number"",""default"":10,""description"":""Gap between the sheet border and the viewport box""}},""required"":[""viewId"",""sheetName""],""additionalProperties"":false}},""atomic"":{""type"":""boolean"",""default"":false,""description"":""Abort the whole batch when any item fails validation""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""items""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var request = AgentJson.Deserialize<BuildSheetsRequest>(args);
        var items = request?.Items;
        if (items is null || items.Count == 0)
        {
            outcome.Success = false;
            outcome.ErrorCode = "no_items";
            outcome.Message = "The items array is empty; pass at least one sheet build item.";
            return;
        }

        if (items.Count > MaxItems)
        {
            outcome.Success = false;
            outcome.ErrorCode = "limit_exceeded";
            outcome.Message = "At most " + MaxItems + " sheet items per call are allowed.";
            return;
        }

        var atomic = AgentJson.GetBool(args, "atomic", false);
        outcome.Description = "Build " + items.Count + " sheet(s)";

        // Viewport.Create returns null on a document that was not regenerated
        // after its last geometry change (IT-verified pattern, CreateViewportsTool).
        doc.Regenerate();

        var viewportOwners = CollectViewportOwners(doc);
        ElementId? defaultTitleBlockId = null;
        if (items.Any(item => (item.TitleBlockTypeId ?? 0) <= 0))
        {
            var available = AgentSheetWriteHelper.CollectTitleBlockTypeIds(doc);
            if (available.Count == 0)
            {
                outcome.Success = false;
                outcome.ErrorCode = "title_block_missing";
                outcome.Message =
                    "The project has no loaded title block type, and ViewSheet.Create needs " +
                    "one. Load a title block family (a .rfa of OST_TitleBlocks) and retry.";
                return;
            }

            defaultTitleBlockId = available[0];
        }

        AgentSheetReader.TryGetAlbumTail(
            doc, out var numberPrefix, out var albumMaxValue, out var numberDigits);
        // The album tail itself is taken: auto-numbers start AFTER it
        // (sheets 1..3 → the first auto item gets 4, not 3).
        var nextValue = albumMaxValue + 1;
        var usedNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Explication clone names must not collide with existing/planned ones.
        var usedScheduleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (items.Any(item => item.Explication is not null))
        {
            foreach (var existing in AgentScheduleReader.Collect(doc))
                usedScheduleNames.Add(existing.Name);
        }

        var rows = new List<object>(items.Count);
        var planned = new List<PlannedItem>();

        // Album inheritance (C6-11): one scan per title block type, shared by
        // every item of the batch.
        var inheritedCache = new Dictionary<long, Dictionary<string, string>?>();

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Id = item.ViewId };
            var plannedItem = ValidateItem(
                doc, outcome, item, report, viewportOwners, defaultTitleBlockId,
                numberPrefix, ref nextValue, numberDigits, usedNumbers, usedScheduleNames,
                inheritedCache);
            if (plannedItem is null)
            {
                rows.Add(new
                {
                    viewId = item.ViewId,
                    sheetNumber = item.SheetNumber,
                    status = "failed",
                    errorCode = report.ErrorCode,
                    reason = report.Reason
                });
                continue;
            }

            planned.Add(plannedItem);
        }

        if (planned.Count == 0)
        {
            outcome.Data["items"] = rows;
            return;
        }

        if (atomic && outcome.Batch.Failed.Count > 0)
        {
            outcome.Aborted = true;
            outcome.Message = "atomic=true: " + outcome.Batch.Failed.Count +
                              " item(s) failed validation; the batch was not applied.";
            outcome.Data["items"] = rows;
            return;
        }

        foreach (var entry in planned)
        {
            ct.ThrowIfCancellationRequested();
            ApplyItem(doc, entry, outcome, rows);
        }

        outcome.Data["items"] = rows;
    }

    /// <summary>
    /// The categories hidden in the view a sheet is built for: the count alone
    /// could not answer "what else may still be visible" (owner feedback
    /// 2026-11-24). Names are the BuiltIn names when known, display names
    /// otherwise, capped at 30 with the honest count.
    /// </summary>
    private static object? HiddenCategoriesOf(View view)
    {
        try
        {
            var settings = AgentViewSettingsReader.Read(view.Document, view);
            var names = settings.HiddenCategories
                .Select(info => info.BuiltInName ?? info.Category)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(30)
                .ToList();
            return new
            {
                count = settings.HiddenCategoryCount,
                names,
                capped = settings.HiddenCategoryListCapped
                         || settings.HiddenCategories.Count > names.Count
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The categories that actually have visible elements in the view. The
    /// hidden list alone let a sheet report built/insideSheet with a stair
    /// still on the view: only this list answers "what is in the picture".
    /// </summary>
    private static object? VisibleCategoriesOf(View view)
    {
        try
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var elementCount = 0;
            var truncated = false;
            foreach (var element in new FilteredElementCollector(view.Document, view.Id)
                         .WhereElementIsNotElementType())
            {
                elementCount++;
                var name = element.Category?.Name;
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name!);
                if (elementCount >= 5000)
                {
                    truncated = true;
                    break;
                }
            }

            var shown = names.Take(30).ToList();
            return new
            {
                count = names.Count,
                names = shown,
                elementCount,
                capped = truncated || names.Count > shown.Count
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void ApplyItem(
        Document doc,
        PlannedItem entry,
        WriteOutcome outcome,
        List<object> rows)
    {
        var report = new WriteItemReport
        {
            Id = entry.View.Id.GetValue(),
            Name = entry.Number + " - " + entry.SheetName
        };

        ViewSheet? sheet = null;
        try
        {
            sheet = ViewSheet.Create(doc, entry.TitleBlockTypeId);
            sheet.SheetNumber = entry.Number;
            sheet.Name = entry.SheetName;

            var itemWarnings = new List<string>();
            var parameterResults = ApplyTitleBlockParameters(
                doc, sheet, entry.TitleBlockParameters,
                entry.InheritedTitleBlockValues, itemWarnings);
            itemWarnings.ForEach(outcome.Warnings.Add);

            var placement = PlaceViewportTopLeft(doc, sheet, entry);
            placement.Warnings.ForEach(warning =>
            {
                itemWarnings.Add(warning);
                outcome.Warnings.Add(warning);
            });

            object? scheduleInfo = null;
            ScheduleSheetInstance? scheduleInstance = null;
            if (entry.Schedule is not null)
            {
                var schedulePlacement = PlaceScheduleRightOfView(
                    doc, sheet, entry.Schedule!, entry.MarginMm, placement);
                scheduleInstance = schedulePlacement.Instance;
                schedulePlacement.Warnings.ForEach(warning =>
                {
                    itemWarnings.Add(warning);
                    outcome.Warnings.Add(warning);
                });

                scheduleInfo = schedulePlacement.Info;
            }

            // Same transaction/change set; a refusal throws → the sheet is discarded below.
            ExplicationPlacement? explication = null;
            if (entry.Explication is not null)
            {
                var explicationWarnings = new List<string>();
                explication = BuildAndPlaceExplication(
                    doc, sheet, entry, placement, explicationWarnings);
                explicationWarnings.ForEach(warning =>
                {
                    itemWarnings.Add(warning);
                    outcome.Warnings.Add(warning);
                });
            }

            report.NewValue = entry.Number;
            report.Detail = "Viewport " +
                            (placement.InsideSheet
                                ? "inside sheet"
                                : "OVERFLOW " + placement.OverflowText)
                            + "; view '" + entry.View.Name + "'"
                            + (scheduleInstance is null
                                ? string.Empty
                                : "; schedule '" + entry.Schedule!.Name + "' placed")
                            + (explication is null
                                ? string.Empty
                                : "; explication '" + explication.Schedule!.Name + "' built");
            outcome.Batch.Changed.Add(report);
            outcome.AffectedCount++;
            outcome.Items.Add(new ChangeItem
            {
                UniqueId = sheet.UniqueId,
                ElementId = sheet.Id.GetValue(),
                ParameterName = "sheet_created",
                NewValue = entry.Number,
                Status = ChangeStatus.Applied
            });
            outcome.Items.Add(new ChangeItem
            {
                UniqueId = placement.Viewport!.UniqueId,
                ElementId = placement.Viewport.Id.GetValue(),
                ParameterName = "viewport_created",
                NewValue = entry.View.Name,
                Status = ChangeStatus.Applied
            });
            if (scheduleInstance is not null)
            {
                outcome.Items.Add(new ChangeItem
                {
                    UniqueId = scheduleInstance.UniqueId,
                    ElementId = scheduleInstance.Id.GetValue(),
                    ParameterName = "schedule_placed",
                    NewValue = entry.Schedule!.Name,
                    Status = ChangeStatus.Applied
                });
            }

            // Undo (reverse order): the clone is deleted first, the cascaded
            // placement is then skipped as already removed.
            if (explication is not null)
            {
                outcome.Items.Add(new ChangeItem
                {
                    UniqueId = explication.Schedule!.UniqueId,
                    ElementId = explication.Schedule.Id.GetValue(),
                    ParameterName = "schedule_created",
                    NewValue = explication.Schedule.Name,
                    Status = ChangeStatus.Applied
                });
                if (explication.Instance is not null)
                {
                    outcome.Items.Add(new ChangeItem
                    {
                        UniqueId = explication.Instance.UniqueId,
                        ElementId = explication.Instance.Id.GetValue(),
                        ParameterName = "schedule_placed",
                        NewValue = explication.Schedule.Name,
                        Status = ChangeStatus.Applied
                    });
                }
            }

            rows.Add(new
            {
                viewId = entry.View.Id.GetValue(),
                viewName = entry.View.Name,
                status = "built",
                sheetId = sheet.Id.GetValue(),
                sheetNumber = sheet.SheetNumber,
                sheetName = sheet.Name,
                titleBlockTypeId = entry.TitleBlockTypeId.GetValue(),
                sheetOutlineMm = AgentSheetReader.SheetOutlineMm(sheet),
                viewportId = placement.Viewport!.Id.GetValue(),
                viewportCenterMm = placement.CenterMm,
                viewportCenterFromFrameMm = placement.CenterFromFrameMm,
                viewportBoxMm = placement.BoxMm,
                insideSheet = placement.InsideSheet,
                overflow = !placement.InsideSheet,
                schedule = scheduleInfo,
                explication = explication?.Info,
                explicationRows = explication?.Rows,
                titleBlockParameters = parameterResults,
                hiddenCategories = HiddenCategoriesOf(entry.View),
                visibleCategories = VisibleCategoriesOf(entry.View),
                warnings = itemWarnings.Count == 0 ? null : itemWarnings
            });
        }
        catch (Exception ex)
        {
            DiscardHalfBuiltSheet(doc, sheet);
            Fail(outcome, report, "build_failed",
                "Revit refused to build the sheet for view '" + entry.View.Name + "': " +
                ex.GetType().Name + ": " + ex.Message);
            rows.Add(new
            {
                viewId = entry.View.Id.GetValue(),
                sheetNumber = entry.Number,
                status = "failed",
                errorCode = report.ErrorCode,
                reason = report.Reason
            });
        }
    }

    /// <summary>
    /// Deletes the half-built sheet of a failed item, so it never leaves an
    /// empty numbered sheet behind (a failed item must not change the model).
    /// </summary>
    private static void DiscardHalfBuiltSheet(Document doc, ViewSheet? sheet)
    {
        if (sheet is null)
            return;

        try
        {
            doc.Delete(sheet.Id);
        }
        catch (Exception)
        {
            // Best effort: an orphan sheet is visible to the user, a thrown
            // exception here would bury the original failure reason.
        }
    }

}
