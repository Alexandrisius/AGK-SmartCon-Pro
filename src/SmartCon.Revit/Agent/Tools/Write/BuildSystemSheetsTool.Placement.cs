using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Placement half of build_system_sheets: title block parameter writes, the
/// measure-and-recenter viewport snap to the top-left corner and the schedule
/// placement to the right of the placed view.
/// </summary>
public sealed partial class BuildSystemSheetsTool
{
    /// <summary>
    /// The viewport box is unknown until the viewport exists, so the view is
    /// placed at a provisional point first, measured after a regeneration and
    /// recentered so the box lands in the top-left corner with the margin
    /// (Autodesk's SetBoxCenter example pattern).
    /// </summary>
    private static ViewportPlacement PlaceViewportTopLeft(
        Document doc,
        ViewSheet sheet,
        PlannedItem entry)
    {
        var sheetOutline = sheet.Outline;
        var minU = sheetOutline.Min.U;
        var minV = sheetOutline.Min.V;
        var maxU = sheetOutline.Max.U;
        var maxV = sheetOutline.Max.V;
        var margin = AgentSheetWriteHelper.MillimetersToInternal(entry.MarginMm);

        var provisional = new XYZ(
            minU + (maxU - minU) / 2,
            minV + (maxV - minV) / 2,
            0);
        var viewport = Viewport.Create(doc, sheet.Id, entry.View.Id, provisional);
        if (viewport is null)
        {
            // Viewport.Create returns null on a document that was not
            // regenerated after its last change (IT-verified pattern).
            doc.Regenerate();
            viewport = Viewport.Create(doc, sheet.Id, entry.View.Id, provisional);
        }

        if (viewport is null)
        {
            throw new InvalidOperationException(
                "Revit returned no viewport for view '" + entry.View.Name + "'.");
        }

        doc.Regenerate();
        var box = viewport.GetBoxOutline();
        var boxWidth = box.MaximumPoint.X - box.MinimumPoint.X;
        var boxHeight = box.MaximumPoint.Y - box.MinimumPoint.Y;
        SmartConLogger.Debug(
            "BuildSystemSheets: sheet " + sheet.SheetNumber +
            " outline minU=" + MillimetersText(AgentUnitsFormat.ToMillimeters(minU)) +
            " minV=" + MillimetersText(AgentUnitsFormat.ToMillimeters(minV)) +
            " maxU=" + MillimetersText(AgentUnitsFormat.ToMillimeters(maxU)) +
            " maxV=" + MillimetersText(AgentUnitsFormat.ToMillimeters(maxV)) +
            " mm; measured box " + MillimetersText(AgentUnitsFormat.ToMillimeters(boxWidth)) +
            "x" + MillimetersText(AgentUnitsFormat.ToMillimeters(boxHeight)) + " mm");
        viewport.SetBoxCenter(new XYZ(
            minU + margin + boxWidth / 2,
            maxV - margin - boxHeight / 2,
            0));
        doc.Regenerate();

        var result = new ViewportPlacement { Viewport = viewport };
        var finalBox = viewport.GetBoxOutline();
        var center = viewport.GetBoxCenter();
        SmartConLogger.Debug(
            "BuildSystemSheets: sheet " + sheet.SheetNumber +
            " final box center " + MillimetersText(AgentUnitsFormat.ToMillimeters(center.X)) +
            ";" + MillimetersText(AgentUnitsFormat.ToMillimeters(center.Y)) +
            " mm, min " + MillimetersText(AgentUnitsFormat.ToMillimeters(finalBox.MinimumPoint.X)) +
            ";" + MillimetersText(AgentUnitsFormat.ToMillimeters(finalBox.MinimumPoint.Y)) +
            " max " + MillimetersText(AgentUnitsFormat.ToMillimeters(finalBox.MaximumPoint.X)) +
            ";" + MillimetersText(AgentUnitsFormat.ToMillimeters(finalBox.MaximumPoint.Y)) + " mm");

        // Inside-sheet check against ViewSheet.Outline with a 1 mm tolerance
        // (paper-space feet; the outline is the full sheet, border included).
        // Overlaps are positive ONLY when the box sticks out: left/bottom
        // compare the border against the box minimum (border - box > 0 → out),
        // right/top compare the box maximum against the border.
        var tolerance = AgentSheetWriteHelper.MillimetersToInternal(1);
        var leftOverlap = minU - finalBox.MinimumPoint.X;
        var bottomOverlap = minV - finalBox.MinimumPoint.Y;
        var rightOverlap = finalBox.MaximumPoint.X - maxU;
        var topOverlap = finalBox.MaximumPoint.Y - maxV;
        var inside = leftOverlap <= tolerance
                     && bottomOverlap <= tolerance
                     && rightOverlap <= tolerance
                     && topOverlap <= tolerance;
        result.InsideSheet = inside;
        result.BoxMaxXFeet = finalBox.MaximumPoint.X;
        result.BoxMaxYFeet = finalBox.MaximumPoint.Y;
        if (!inside)
        {
            var parts = new List<string>();
            if (leftOverlap > tolerance)
                parts.Add("left " + MillimetersText(AgentUnitsFormat.ToMillimeters(leftOverlap)) + " mm");
            if (bottomOverlap > tolerance)
                parts.Add("bottom " + MillimetersText(AgentUnitsFormat.ToMillimeters(bottomOverlap)) + " mm");
            if (rightOverlap > tolerance)
                parts.Add("right " + MillimetersText(AgentUnitsFormat.ToMillimeters(rightOverlap)) + " mm");
            if (topOverlap > tolerance)
                parts.Add("top " + MillimetersText(AgentUnitsFormat.ToMillimeters(topOverlap)) + " mm");

            result.OverflowText = string.Join(", ", parts);
            result.Warnings.Add(
                "Sheet " + sheet.SheetNumber + ": the viewport box of view '" +
                entry.View.Name + "' exceeds the sheet border by " + result.OverflowText +
                ". [Action: move the viewport with move_viewport, or rebuild the sheet " +
                "with a larger title block format parameter]");
        }

        result.CenterMm = new
        {
            x = AgentUnitsFormat.ToMillimeters(center.X),
            y = AgentUnitsFormat.ToMillimeters(center.Y)
        };
        result.BoxMm = new
        {
            minX = AgentUnitsFormat.ToMillimeters(finalBox.MinimumPoint.X),
            minY = AgentUnitsFormat.ToMillimeters(finalBox.MinimumPoint.Y),
            maxX = AgentUnitsFormat.ToMillimeters(finalBox.MaximumPoint.X),
            maxY = AgentUnitsFormat.ToMillimeters(finalBox.MaximumPoint.Y),
            width = AgentUnitsFormat.ToMillimeters(
                finalBox.MaximumPoint.X - finalBox.MinimumPoint.X),
            height = AgentUnitsFormat.ToMillimeters(
                finalBox.MaximumPoint.Y - finalBox.MinimumPoint.Y)
        };
        return result;
    }

    /// <summary>
    /// Places the schedule at the top edge of the view, to its right. The
    /// actual size is read back through the instance bounding box (it includes
    /// Revit's fixed ~2.1 mm selection margin around the table). Used both for
    /// the ready scheduleId path and for the explication clone.
    /// </summary>
    private static SchedulePlacement PlaceScheduleRightOfView(
        Document doc,
        ViewSheet sheet,
        ViewSchedule schedule,
        double marginMm,
        ViewportPlacement placement)
    {
        var margin = AgentSheetWriteHelper.MillimetersToInternal(marginMm);
        var point = new XYZ(placement.BoxMaxXFeet + margin, placement.BoxMaxYFeet, 0);

        doc.Regenerate();
        var instance = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, point);
        doc.Regenerate();

        var result = new SchedulePlacement { Instance = instance };
        var actual = instance.Point;
        double? widthMm = null;
        double? heightMm = null;
        try
        {
            var boundingBox = instance.get_BoundingBox(sheet);
            if (boundingBox is not null)
            {
                widthMm = AgentUnitsFormat.ToMillimeters(
                    boundingBox.Max.X - boundingBox.Min.X);
                heightMm = AgentUnitsFormat.ToMillimeters(
                    boundingBox.Max.Y - boundingBox.Min.Y);

                var tolerance = AgentSheetWriteHelper.MillimetersToInternal(1);
                if (boundingBox.Max.X > sheet.Outline.Max.U + tolerance)
                {
                    result.Warnings.Add(
                        "Sheet " + sheet.SheetNumber + ": schedule '" + schedule.Name +
                        "' placed right of the view reaches beyond the sheet border. " +
                        "[Action: move it with move_schedule_on_sheet or shrink the " +
                        "schedule columns]");
                }
            }
        }
        catch (Exception)
        {
            // The instance is valid even without a readable bounding box; the
            // placement point alone is reported then.
        }

        result.Info = new
        {
            scheduleId = schedule.Id.GetValue(),
            placed = true,
            instanceId = instance.Id.GetValue(),
            pointMm = new
            {
                x = AgentUnitsFormat.ToMillimeters(actual.X),
                y = AgentUnitsFormat.ToMillimeters(actual.Y)
            },
            widthMm,
            heightMm,
            note = widthMm is null
                ? "The schedule size could not be read back (no bounding box)."
                : null
        };
        return result;
    }

    /// <summary>
    /// Writes titleBlockParameters to the title block INSTANCE of the new
    /// sheet. A missing or rejected parameter is a warning (titleblock_
    /// parameter_not_found / the setter reason) — the sheet itself stays.
    /// </summary>
    private static List<object> ApplyTitleBlockParameters(
        Document doc,
        ViewSheet sheet,
        Dictionary<string, JsonElement>? parameters,
        List<string> warnings)
    {
        var results = new List<object>();
        if (parameters is null || parameters.Count == 0)
            return results;

        var instance = FindTitleBlockInstance(doc, sheet);
        if (instance is null)
        {
            warnings.Add(
                "Sheet " + sheet.SheetNumber + " has no title block instance; " +
                "titleBlockParameters were not applied. [Action: check the title block " +
                "family of the sheet type]");
            foreach (var name in parameters.Keys)
            {
                results.Add(new
                {
                    name,
                    status = "failed",
                    errorCode = "titleblock_not_found"
                });
            }

            return results;
        }

        foreach (var pair in parameters)
        {
            var parameter = FindInstanceParameter(instance, pair.Key);
            if (parameter is null)
            {
                results.Add(new
                {
                    name = pair.Key,
                    status = "failed",
                    errorCode = "titleblock_parameter_not_found"
                });
                warnings.Add(
                    "Sheet " + sheet.SheetNumber + ": title block parameter '" + pair.Key +
                    "' was not found on the title block instance. [Action: check the exact " +
                    "editable name via get_titleblock_options(sheetId)]");
                continue;
            }

            var text = JsonValueToString(pair.Value);
            var unit = parameter.StorageType == StorageType.Double
                       && pair.Value.ValueKind == JsonValueKind.Number
                ? "raw"
                : null;
            if (AgentParameterWriteHelper.TrySet(parameter, text, unit, out var errorCode, out var reason))
            {
                results.Add(new { name = pair.Key, status = "applied", value = text });
                continue;
            }

            results.Add(new { name = pair.Key, status = "failed", errorCode, reason });
            warnings.Add(
                "Sheet " + sheet.SheetNumber + ": title block parameter '" + pair.Key +
                "' was not set (" + reason + "). [Action: pass a value matching the " +
                "parameter storage type " + parameter.StorageType + "]");
        }

        return results;
    }

    private static FamilyInstance? FindTitleBlockInstance(Document doc, ViewSheet sheet)
    {
        foreach (var element in new FilteredElementCollector(doc, sheet.Id)
                     .OfCategory(BuiltInCategory.OST_TitleBlocks)
                     .WhereElementIsNotElementType())
        {
            if (element is FamilyInstance instance)
                return instance;
        }

        return null;
    }

    /// <summary>Every title block instance of the document, sheeted or not.</summary>
    private static List<FamilyInstance> CollectTitleBlockInstances(Document doc)
    {
        var instances = new List<FamilyInstance>();
        foreach (var element in new FilteredElementCollector(doc)
                     .OfCategory(BuiltInCategory.OST_TitleBlocks)
                     .WhereElementIsNotElementType())
        {
            if (element is FamilyInstance instance)
                instances.Add(instance);
        }

        return instances;
    }

    /// <summary>
    /// Sheets that already hold a viewport of a view, by view id (legends may
    /// repeat; owners here block re-placement of regular views).
    /// </summary>
    private static Dictionary<long, string> CollectViewportOwners(Document doc)
    {
        var owners = new Dictionary<long, string>();
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(Viewport)))
        {
            if (element is not Viewport viewport)
                continue;

            var ownerSheetId = viewport.SheetId;
            if (ownerSheetId == ElementId.InvalidElementId)
                continue;

            owners[viewport.ViewId.GetValue()] =
                (doc.GetElement(ownerSheetId) as ViewSheet)?.SheetNumber ?? string.Empty;
        }

        return owners;
    }

    /// <summary>
    /// Values a new sheet inherits from the album: for every editable instance
    /// parameter of the title block that is not a built-in parameter and holds
    /// the same value on ALL existing sheets of the same title block type,
    /// that common value. Built-in sheet parameters (number, name, scale...)
    /// are never inherited: the tool or the user owns them. Null when the
    /// album has no sheet of this type yet.
    /// </summary>
    private static Dictionary<string, string>? InheritedTitleBlockValues(
        Document doc,
        ElementId titleBlockTypeId)
    {
        var instances = new List<FamilyInstance>();
        foreach (var instance in CollectTitleBlockInstances(doc))
        {
            if (instance.GetTypeId() == titleBlockTypeId)
                instances.Add(instance);
        }

        if (instances.Count == 0)
            return null;

        var candidates = new Dictionary<Parameter, string>();
        foreach (Parameter parameter in instances[0].Parameters)
        {
            var name = parameter.Definition?.Name;
            if (string.IsNullOrEmpty(name) || parameter.IsReadOnly)
                continue;

            // A built-in parameter is an InternalDefinition with a BuiltInParameter value.
            if (parameter.Definition is InternalDefinition { BuiltInParameter: not BuiltInParameter.INVALID })
                continue;

            // ElementId values (a level, a type) are not portable between sheets.
            if (parameter.StorageType == StorageType.ElementId)
                continue;

            candidates[parameter] = name!;
        }

        if (candidates.Count == 0)
            return null;

        var inherited = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in candidates)
        {
            var value = pair.Key.HasValue ? DescribeOf(pair.Key) : null;
            if (string.IsNullOrEmpty(value))
                continue;

            var same = true;
            for (var index = 1; index < instances.Count && same; index++)
            {
                var other = FindInstanceParameter(instances[index], pair.Value);
                same = other is not null && other.HasValue
                       && string.Equals(DescribeOf(other), value, StringComparison.Ordinal);
            }

            if (same)
                inherited.Add(pair.Value, value!);
        }

        return inherited;
    }

    private static string? DescribeOf(Parameter parameter) =>
        parameter.StorageType == StorageType.Double
            ? parameter.AsDouble().ToString("R", CultureInfo.InvariantCulture)
            : parameter.StorageType == StorageType.Integer
                ? parameter.AsInteger().ToString(CultureInfo.InvariantCulture)
                : parameter.AsString();

    private static Parameter? FindInstanceParameter(FamilyInstance instance, string name)
    {
        var byLookup = instance.LookupParameter(name);
        if (byLookup is not null)
            return byLookup;

        var named = instance.GetParameters(name);
        return named.Count > 0 ? named[0] : null;
    }

    private static string? JsonValueToString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => null,
        _ => value.GetRawText()
    };

    private static string MillimetersText(double value) =>
        value.ToString("0.0##", CultureInfo.InvariantCulture);

    private sealed class ViewportPlacement
    {
        public Viewport? Viewport { get; set; }

        public object? CenterMm { get; set; }

        public object? BoxMm { get; set; }

        public bool InsideSheet { get; set; }

        public string OverflowText { get; set; } = string.Empty;

        public double BoxMaxXFeet { get; set; }

        public double BoxMaxYFeet { get; set; }

        public List<string> Warnings { get; } = new();
    }

    private sealed class SchedulePlacement
    {
        public ScheduleSheetInstance? Instance { get; set; }

        public object? Info { get; set; }

        public List<string> Warnings { get; } = new();
    }
}
