using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Removes invisible code units from sheet numbers. A sheet whose cleaned
/// number would collide with another sheet (raw or visually) is reported as
/// sheet_number_taken and skipped; the rest of the batch still applies.
/// </summary>
public sealed class SanitizeSheetNumbersTool : AgentWriteToolBase
{
    public SanitizeSheetNumbersTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "sanitize_sheet_numbers",
        Description =
            "Fixes sheet numbers that contain invisible characters (zero-width or bidi format " +
            "code units, listed by list_sheets as hasInvisibleCharacters/numberCodePoints): the " +
            "number is rewritten in its visible form. Without sheetIds every affected sheet of " +
            "the document is processed. When the cleaned number would collide with another " +
            "sheet's number (including a visual match), that sheet is skipped with " +
            "sheet_number_taken and the rest still applies. Idempotent; dry run defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""sheetIds"":{""type"":""array"",""description"":""Sheet ids to sanitize; default: every sheet with invisible characters in the number"",""items"":{""type"":""integer""}},""dryRun"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var requestedIds = AgentJson.GetLongArray(args, "sheetIds");
        var allSheets = AgentSheetReader.Collect(doc, excludePlaceholders: false);
        outcome.Description = "Sanitize sheet numbers" +
                              (requestedIds.Count > 0 ? " (" + requestedIds.Count + " requested)" : "");

        List<ViewSheet>? selection = null;
        if (requestedIds.Count > 0)
        {
            selection = new List<ViewSheet>(requestedIds.Count);
            foreach (var requestedId in requestedIds)
            {
                var report = new WriteItemReport { Id = requestedId };
                var sheet = allSheets.FirstOrDefault(
                    candidate => candidate.Id.GetValue() == requestedId);
                if (sheet is null)
                {
                    report.ErrorCode = "sheet_not_found";
                    report.Reason = "Element " + requestedId + " is not a sheet.";
                    outcome.Batch.Failed.Add(report);
                    continue;
                }

                selection.Add(sheet);
            }
        }

        var targets = new List<ViewSheet>();
        foreach (var sheet in selection ?? (IEnumerable<ViewSheet>)allSheets)
        {
            if (AgentTextAudit.HasInvisibleCharacters(sheet.SheetNumber))
                targets.Add(sheet);
        }

        if (targets.Count == 0)
        {
            outcome.Data["message"] =
                "No sheet numbers contain invisible characters (nothing to sanitize).";
            return;
        }

        // Effective numbers start as the current values and are updated as sheets
        // are cleaned, so two sheets cleaning towards the same visible number
        // produce one applied change and one typed conflict.
        var effectiveNumbers = new Dictionary<long, string>(allSheets.Count);
        foreach (var sheet in allSheets)
            effectiveNumbers[sheet.Id.GetValue()] = sheet.SheetNumber ?? string.Empty;

        foreach (var sheet in targets)
        {
            ct.ThrowIfCancellationRequested();

            var raw = sheet.SheetNumber ?? string.Empty;
            var cleaned = AgentTextAudit.Visual(raw);
            var report = new WriteItemReport
            {
                Id = sheet.Id.GetValue(),
                Name = AgentSheetWriteHelper.SheetLabel(sheet),
                OldValue = raw,
                Detail = "Code points before: " + string.Join(", ",
                    AgentTextAudit.CodePoints(raw).Select(point => "U+" + point.ToString("X4")))
            };

            if (string.IsNullOrWhiteSpace(cleaned))
            {
                report.ErrorCode = "sheet_number_invalid";
                report.Reason = "After removing the invisible characters the number would be " +
                                "empty; assign a new number with set_sheet_number instead.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            var collisionSheetId = FindVisualOwner(effectiveNumbers, cleaned, sheet.Id.GetValue());
            if (collisionSheetId.HasValue)
            {
                report.ErrorCode = "sheet_number_taken";
                report.Reason = "The cleaned number '" + cleaned + "' is already used by sheet " +
                                "id " + collisionSheetId.Value +
                                " (visual match); resolve the duplicate manually.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            sheet.SheetNumber = cleaned;
            effectiveNumbers[sheet.Id.GetValue()] = cleaned;
            report.NewValue = sheet.SheetNumber;
            outcome.Batch.Changed.Add(report);
            outcome.AffectedCount++;
            outcome.Items.Add(new ChangeItem
            {
                UniqueId = sheet.UniqueId,
                ElementId = sheet.Id.GetValue(),
                ParameterName = "sheet_number",
                OldValue = raw,
                NewValue = sheet.SheetNumber,
                Status = ChangeStatus.Applied
            });
        }
    }

    private static long? FindVisualOwner(
        Dictionary<long, string> effectiveNumbers,
        string cleaned,
        long excludeSheetId)
    {
        foreach (var pair in effectiveNumbers)
        {
            if (pair.Key == excludeSheetId)
                continue;

            if (string.Equals(pair.Value, cleaned, StringComparison.OrdinalIgnoreCase))
                return pair.Key;

            if (string.Equals(
                    AgentTextAudit.Visual(pair.Value),
                    cleaned,
                    StringComparison.OrdinalIgnoreCase))
            {
                return pair.Key;
            }
        }

        return null;
    }
}
