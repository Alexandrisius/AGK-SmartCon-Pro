using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Renumbers sheets in one batch through the rename planner: temporary numbers
/// break swap cycles, so inserting a number into the middle of a sequence never
/// hits an intermediate collision. The order of sheetIds is the target order.
/// </summary>
public sealed class RenumberSheetsTool : AgentWriteToolBase
{
    private const int MaxSheets = 50;

    public RenumberSheetsTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "renumber_sheets",
        Description =
            "Renumbers up to 50 sheets in one call (one transaction, one change set). " +
            "sheetIds[] carries the TARGET order: sheetIds[i] gets numbers[i]. Instead of " +
            "numbers[] pass startNumber ('ОВ-5' → ОВ-5, ОВ-6, ... : leading prefix plus a " +
            "number, incremented per sheet). Renumbering goes through temporary numbers, so " +
            "inserting a number into the middle of a sequence (ОВ-1, ОВ-2 → ОВ-1, ОВ-3, ОВ-2) " +
            "never collides halfway. Invalid numbers (empty, forbidden or invisible " +
            "characters) → sheet_number_invalid per sheet; a number held by a sheet outside " +
            "the batch → sheet_number_taken per sheet with the owner. Numbers released by the " +
            "batch itself are reused freely. dry run defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""sheetIds"":{""type"":""array"",""minItems"":1,""maxItems"":50,""items"":{""type"":""integer""},""description"":""Sheets in the TARGET order: sheetIds[i] receives numbers[i]""},""numbers"":{""type"":""array"",""minItems"":1,""maxItems"":50,""items"":{""type"":""string""},""description"":""New numbers in the sheetIds order""},""startNumber"":{""type"":""string"",""description"":""Prefix plus start number ('ОВ-5' → ОВ-5, ОВ-6, ...); replaces numbers[]""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""sheetIds""],""anyOf"":[{""required"":[""numbers""]},{""required"":[""startNumber""]}],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var sheetIds = AgentJson.GetLongArray(args, "sheetIds");
        if (sheetIds.Count == 0)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "sheetIds is empty; pass at least one sheet id.";
            return;
        }

        if (sheetIds.Count > MaxSheets)
        {
            outcome.Success = false;
            outcome.ErrorCode = "limit_exceeded";
            outcome.Message = "At most " + MaxSheets + " sheets per call are allowed.";
            return;
        }

        if (sheetIds.Distinct().Count() != sheetIds.Count)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "sheetIds contains duplicates; every sheet may be listed once.";
            return;
        }

        var numbers = ResolveNumbers(args, sheetIds.Count, out var numberError);
        if (numbers is null)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = numberError!;
            return;
        }

        if (numbers.Distinct(StringComparer.Ordinal).Count() != numbers.Count)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "The new numbers contain duplicates; every target number must " +
                              "be unique within the batch.";
            return;
        }

        outcome.Description = "Renumber " + sheetIds.Count + " sheet(s)";

        var batchIds = new HashSet<long>(sheetIds);
        var pending = new List<(ViewSheet Sheet, WriteItemReport Report, string Current)>();
        var rows = new List<object>(sheetIds.Count);

        for (var index = 0; index < sheetIds.Count; index++)
        {
            ct.ThrowIfCancellationRequested();

            var sheetId = sheetIds[index];
            var target = numbers[index];
            var report = new WriteItemReport { Id = sheetId };

            if (doc.GetElement(ElementIdCompat.Create(sheetId)) is not ViewSheet sheet)
            {
                Fail(outcome, report, "sheet_not_found",
                    "Element " + sheetId + " is not a sheet.");
                rows.Add(new { sheetId, number = target, status = "failed" });
                continue;
            }

            report.Name = AgentSheetWriteHelper.SheetLabel(sheet);
            report.NewValue = target;

            var invalidReason = AgentSheetWriteHelper.ValidateNumber(target);
            if (invalidReason is not null)
            {
                Fail(outcome, report, "sheet_number_invalid", invalidReason);
                rows.Add(new { sheetId, number = target, status = "failed" });
                continue;
            }

            var current = sheet.SheetNumber ?? string.Empty;
            report.OldValue = current;

            if (string.Equals(current, target, StringComparison.Ordinal))
            {
                report.Reason = "The sheet already has this number (no-op).";
                outcome.Batch.Skipped.Add(report);
                rows.Add(new { sheetId, number = target, status = "skipped" });
                continue;
            }

            var owner = AgentSheetWriteHelper.FindNumberOwner(doc, target, sheet.Id.GetValue());
            if (owner is not null && !batchIds.Contains(owner.Id.GetValue()))
            {
                Fail(outcome, report, "sheet_number_taken",
                    "Number '" + target + "' is held by sheet " +
                    AgentSheetWriteHelper.SheetLabel(owner) + " (id " + owner.Id.GetValue() +
                    ") which is not part of the batch.");
                rows.Add(new
                {
                    sheetId,
                    number = target,
                    owner = new
                    {
                        sheetId = owner.Id.GetValue(),
                        number = owner.SheetNumber,
                        name = owner.Name
                    },
                    status = "failed"
                });
                continue;
            }

            pending.Add((sheet, report, current));
        }

        ApplyPlannedRenumbers(doc, pending, outcome, rows, ct);
        outcome.Data["items"] = rows;
    }

    private static void ApplyPlannedRenumbers(
        Document doc,
        List<(ViewSheet Sheet, WriteItemReport Report, string Current)> pending,
        WriteOutcome outcome,
        List<object> rows,
        CancellationToken ct)
    {
        if (pending.Count == 0)
            return;

        var entries = pending
            .Select(pair => new AgentRenamePlanner.Entry
            {
                Id = pair.Sheet.Id.GetValue(),
                CurrentName = pair.Current,
                NewName = pair.Report.NewValue!
            })
            .ToList();

        var documentNumbers = new List<string>();
        foreach (var sheet in AgentSheetReader.Collect(doc, excludePlaceholders: false))
            documentNumbers.Add(sheet.SheetNumber ?? string.Empty);

        var plan = AgentRenamePlanner.Build(entries, documentNumbers);

        foreach (var entry in plan.TempFirst)
        {
            if (doc.GetElement(ElementIdCompat.Create(entry.Id)) is ViewSheet temp)
                temp.SheetNumber = AgentRenamePlanner.TempName();
        }

        var succeeded = new HashSet<long>();
        foreach (var entry in plan.Order)
        {
            ct.ThrowIfCancellationRequested();

            var pair = pending.First(candidate => candidate.Sheet.Id.GetValue() == entry.Id);
            var report = pair.Report;
            try
            {
                pair.Sheet.SheetNumber = entry.NewName;
                outcome.Batch.Changed.Add(report);
                outcome.AffectedCount++;
                succeeded.Add(entry.Id);
                outcome.Items.Add(new ChangeItem
                {
                    UniqueId = pair.Sheet.UniqueId,
                    ElementId = entry.Id,
                    ParameterName = "sheet_number",
                    OldValue = pair.Current,
                    NewValue = entry.NewName,
                    Status = ChangeStatus.Applied
                });
                rows.Add(new { sheetId = entry.Id, number = entry.NewName, status = "renumbered" });
            }
            catch (Exception ex)
            {
                Fail(outcome, report, "renumber_failed",
                    "Revit refused to set number '" + entry.NewName + "': " + ex.Message);
                rows.Add(new { sheetId = entry.Id, number = entry.NewName, status = "failed" });
            }
        }

        var planned = new HashSet<long>(plan.Order.Select(entry => entry.Id));
        foreach (var pair in pending.Where(pair => !planned.Contains(pair.Sheet.Id.GetValue())))
        {
            Fail(outcome, pair.Report, "sheet_number_taken",
                "The previous number '" + pair.Report.OldValue +
                "' of another batch sheet could not be released; the number was not changed.");
            rows.Add(new
            {
                sheetId = pair.Sheet.Id.GetValue(),
                number = pair.Report.NewValue,
                status = "failed"
            });
        }

        foreach (var entry in plan.TempFirst)
        {
            if (succeeded.Contains(entry.Id))
                continue;

            if (doc.GetElement(ElementIdCompat.Create(entry.Id)) is not ViewSheet sheet)
                continue;

            var current = sheet.SheetNumber ?? string.Empty;
            if (!current.StartsWith(AgentRenamePlanner.TempPrefix, StringComparison.Ordinal))
                continue;

            var pair = pending.First(candidate => candidate.Sheet.Id.GetValue() == entry.Id);
            try
            {
                sheet.SheetNumber = pair.Current;
            }
            catch (Exception ex)
            {
                outcome.Warnings.Add(
                    "Sheet " + entry.Id + " is left with the temporary number '" + current +
                    "': " + ex.Message + " [Action: set the number manually in Revit]");
            }
        }
    }

    /// <summary>
    /// numbers[] as passed, or the sequence generated from startNumber
    /// ("ОВ-5" → ОВ-5, ОВ-6, ... with the zero padding of the source kept).
    /// </summary>
    private static List<string>? ResolveNumbers(
        JsonElement args,
        int count,
        out string? error)
    {
        error = null;
        var hasNumbers = args.TryGetProperty("numbers", out _);
        var startNumber = AgentJson.GetString(args, "startNumber");

        if (hasNumbers && !string.IsNullOrWhiteSpace(startNumber))
        {
            error = "Pass either numbers[] or startNumber, not both.";
            return null;
        }

        if (hasNumbers)
        {
            if (args.TryGetProperty("numbers", out var numbersElement)
                && numbersElement.ValueKind != JsonValueKind.Array)
            {
                error = "numbers must be an array of new sheet numbers.";
                return null;
            }

            var numbers = new List<string>(count);
            if (numbersElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in numbersElement.EnumerateArray())
                    numbers.Add(item.ValueKind == JsonValueKind.String
                        ? item.GetString() ?? string.Empty
                        : string.Empty);
            }

            if (numbers.Count != count)
            {
                error = "numbers[] must have the same length as sheetIds[] (" + count + ").";
                return null;
            }

            return numbers;
        }

        if (string.IsNullOrWhiteSpace(startNumber))
        {
            error = "Pass numbers[] or startNumber.";
            return null;
        }

        var digitsStart = startNumber!.Length;
        while (digitsStart > 0 && char.IsDigit(startNumber[digitsStart - 1]))
            digitsStart--;

        var digits = startNumber.Length - digitsStart;
        if (digits == 0)
        {
            error = "startNumber must end with a number ('ОВ-5'), got '" + startNumber + "'.";
            return null;
        }

        var prefix = startNumber[..digitsStart];
        if (!int.TryParse(
                startNumber.Substring(digitsStart, digits),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var start))
        {
            error = "The number part of startNumber '" + startNumber + "' is too large.";
            return null;
        }

        var padding = digits > start.ToString(CultureInfo.InvariantCulture).Length
            ? "D" + digits.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        var generated = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            var value = start + index;
            generated.Add(prefix + (padding.Length == 0
                ? value.ToString(CultureInfo.InvariantCulture)
                : value.ToString(padding, CultureInfo.InvariantCulture)));
        }

        return generated;
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
        outcome.ErrorCode ??= errorCode;
    }
}
