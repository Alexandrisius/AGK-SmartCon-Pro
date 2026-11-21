using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Shared resolution and validation for sheet write tools: sheet lookup through
/// <see cref="AgentSheetReader.TryFind"/>, sheet-number rules (forbidden
/// characters, invisible code units, raw and visual duplicates) and title-block
/// type discovery.
/// </summary>
internal static class AgentSheetWriteHelper
{
    private static readonly char[] ForbiddenNumberChars =
        { '{', '}', '[', ']', '|', ';', '<', '>', '?', '~' };

    internal static bool TryResolveSheet(
        Document doc,
        JsonElement args,
        out ViewSheet? sheet,
        out IReadOnlyList<SheetCard> candidates,
        out string? errorCode,
        out string? message)
    {
        sheet = null;
        candidates = Array.Empty<SheetCard>();
        var sheetId = AgentJson.GetLong(args, "sheetId", 0);
        var number = AgentJson.GetString(args, "sheetNumber");
        var name = AgentJson.GetString(args, "sheetName");

        if (sheetId <= 0 && string.IsNullOrWhiteSpace(number) && string.IsNullOrWhiteSpace(name))
        {
            errorCode = "invalid_arguments";
            message = "Pass sheetId, sheetNumber or sheetName to identify the sheet.";
            return false;
        }

        if (AgentSheetReader.TryFind(
                doc, sheetId > 0 ? sheetId : null, number, name,
                out sheet, out candidates, out var error))
        {
            errorCode = null;
            message = null;
            return true;
        }

        errorCode = candidates.Count > 0 ? "ambiguous_sheet" : "sheet_not_found";
        message = error;
        return false;
    }

    /// <summary>
    /// Returns an error reason when the sheet number cannot be assigned: empty,
    /// a character Revit refuses, or invisible code units that would create a
    /// number nobody can distinguish (the sanitizer exists for those).
    /// </summary>
    internal static string? ValidateNumber(string? number)
    {
        if (string.IsNullOrWhiteSpace(number))
            return "The sheet number is empty.";

        if (number!.IndexOfAny(ForbiddenNumberChars) >= 0)
            return "The sheet number contains one of the characters Revit refuses: " +
                   "{ } [ ] | ; < > ? ~";

        if (AgentTextAudit.HasInvisibleCharacters(number))
            return "The sheet number contains invisible characters (control or format code " +
                   "units); assign the visible form or run sanitize_sheet_numbers first.";

        return null;
    }

    /// <summary>
    /// The sheet that already owns the number, comparing both the raw value and
    /// the visible glyphs: Revit does not treat numbers that differ only by an
    /// invisible code unit as duplicates.
    /// </summary>
    internal static ViewSheet? FindNumberOwner(Document doc, string number, long excludeSheetId)
    {
        foreach (var candidate in AgentSheetReader.Collect(doc, excludePlaceholders: false))
        {
            if (candidate.Id.GetValue() == excludeSheetId)
                continue;

            if (string.Equals(candidate.SheetNumber, number, StringComparison.OrdinalIgnoreCase))
                return candidate;

            if (string.Equals(
                    AgentTextAudit.Visual(candidate.SheetNumber),
                    AgentTextAudit.Visual(number),
                    StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    internal static IReadOnlyList<ElementId> CollectTitleBlockTypeIds(Document doc) =>
        new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsElementType()
            .ToElementIds()
            .ToList();

    internal static ElementId? FindTitleBlockType(
        Document doc,
        long explicitTypeId,
        out string? errorCode,
        out string? message)
    {
        foreach (var typeId in CollectTitleBlockTypeIds(doc))
        {
            if (typeId.GetValue() == explicitTypeId)
            {
                errorCode = null;
                message = null;
                return typeId;
            }
        }

        errorCode = "invalid_title_block_type";
        message = "Element " + explicitTypeId +
                  " is not a title block type (FamilySymbol of OST_TitleBlocks).";
        return null;
    }

    internal static string SheetLabel(ViewSheet sheet) =>
        sheet.SheetNumber + " - " + sheet.Name;

    internal static string CenterText(double mmX, double mmY) =>
        mmX.ToString("0.0###", CultureInfo.InvariantCulture) + ";" +
        mmY.ToString("0.0###", CultureInfo.InvariantCulture);

    internal static bool TryParseCenter(string? text, out double mmX, out double mmY)
    {
        mmX = 0;
        mmY = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text!.Split(';');
        if (parts.Length != 2)
            return false;

        return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out mmX)
               && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out mmY);
    }

    /// <summary>
    /// Serialized placement of a removed viewport ("viewId;sheetId;x;y" with the
    /// center in millimetres) — enough for undo_last to re-create the viewport.
    /// </summary>
    internal static string PlacementText(long viewId, long sheetId, double mmX, double mmY) =>
        viewId.ToString(CultureInfo.InvariantCulture) + ";" +
        sheetId.ToString(CultureInfo.InvariantCulture) + ";" +
        CenterText(mmX, mmY);

    internal static bool TryParsePlacement(
        string? text,
        out long viewId,
        out long sheetId,
        out double mmX,
        out double mmY)
    {
        viewId = 0;
        sheetId = 0;
        mmX = 0;
        mmY = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text!.Split(';');
        if (parts.Length != 4)
            return false;

        return long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out viewId)
               && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out sheetId)
               && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out mmX)
               && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out mmY);
    }

    internal static double MillimetersToInternal(double mm) =>
        UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
}
