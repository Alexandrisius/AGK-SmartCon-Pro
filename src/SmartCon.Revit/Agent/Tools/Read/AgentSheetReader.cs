using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent;

namespace SmartCon.Revit.Agent.Tools.Read;

internal sealed class SheetCard
{
    public long SheetId { get; set; }

    public string UniqueId { get; set; } = string.Empty;

    public string Number { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public double? WidthMm { get; set; }

    public double? HeightMm { get; set; }

    public int ViewCount { get; set; }

    public int ScheduleCount { get; set; }

    public bool IsPlaceholder { get; set; }
}

internal static class AgentSheetReader
{
    internal static IReadOnlyList<ViewSheet> Collect(Document doc, bool excludePlaceholders)
    {
        var sheets = new List<ViewSheet>();
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)))
        {
            if (element is not ViewSheet sheet)
                continue;
            if (sheet.IsTemplate)
                continue;
            if (excludePlaceholders && sheet.IsPlaceholder)
                continue;
            sheets.Add(sheet);
        }

        return sheets;
    }

    internal static IReadOnlyDictionary<long, int> CountPlacedSchedules(Document doc)
    {
        var counts = new Dictionary<long, int>();
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)))
        {
            if (element is not ScheduleSheetInstance instance)
                continue;

            var owner = instance.OwnerViewId;
            if (owner is null || owner == ElementId.InvalidElementId)
                continue;

            var key = owner.GetValue();
            counts.TryGetValue(key, out var current);
            counts[key] = current + 1;
        }

        return counts;
    }

    internal static SheetCard CreateCard(
        ViewSheet sheet,
        IReadOnlyDictionary<long, int> scheduleCounts)
    {
        double? widthMm = null;
        double? heightMm = null;
        try
        {
            var outline = sheet.Outline;
            if (outline is not null)
            {
                var width = outline.Max.U - outline.Min.U;
                var height = outline.Max.V - outline.Min.V;
                if (width > 0 && height > 0)
                {
                    widthMm = AgentUnitsFormat.ToMillimeters(width);
                    heightMm = AgentUnitsFormat.ToMillimeters(height);
                }
            }
        }
        catch (Exception)
        {
            // Placeholder sheets and empty sheets report a zero outline.
        }

        var sheetId = sheet.Id.GetValue();
        scheduleCounts.TryGetValue(sheetId, out var scheduleCount);

        return new SheetCard
        {
            SheetId = sheetId,
            UniqueId = sheet.UniqueId,
            Number = sheet.SheetNumber ?? string.Empty,
            Name = sheet.Name,
            WidthMm = widthMm,
            HeightMm = heightMm,
            ViewCount = sheet.GetAllPlacedViews().Count,
            ScheduleCount = scheduleCount,
            IsPlaceholder = sheet.IsPlaceholder
        };
    }

    /// <summary>
    /// Finds exactly one sheet by id, number or name. Returns false with a
    /// non-null error when nothing matched, and fills <paramref name="candidates"/>
    /// when the key is ambiguous.
    /// </summary>
    internal static bool TryFind(
        Document doc,
        long? sheetId,
        string? number,
        string? name,
        out ViewSheet? sheet,
        out IReadOnlyList<SheetCard> candidates,
        out string? error)
    {
        sheet = null;
        candidates = Array.Empty<SheetCard>();
        error = null;

        if (sheetId is > 0)
        {
            sheet = doc.GetElement(ElementIdCompat.Create(sheetId.Value)) as ViewSheet;
            if (sheet is null)
            {
                error = "Sheet " + sheetId + " does not exist.";
                return false;
            }

            return true;
        }

        var all = Collect(doc, excludePlaceholders: false);
        var matches = new List<ViewSheet>();
        foreach (var candidate in all)
        {
            if (!string.IsNullOrWhiteSpace(number))
            {
                if (string.Equals(candidate.SheetNumber, number, StringComparison.OrdinalIgnoreCase))
                    matches.Add(candidate);
                continue;
            }

            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                matches.Add(candidate);
        }

        if (matches.Count == 1)
        {
            sheet = matches[0];
            return true;
        }

        if (matches.Count == 0 && !string.IsNullOrWhiteSpace(name))
        {
            foreach (var candidate in all)
            {
                if (candidate.Name.IndexOf(name!, StringComparison.OrdinalIgnoreCase) >= 0)
                    matches.Add(candidate);
            }

            if (matches.Count == 1)
            {
                sheet = matches[0];
                return true;
            }
        }

        if (matches.Count == 0)
        {
            error = "No sheet matches " +
                (string.IsNullOrWhiteSpace(number) ? "name '" + name + "'" : "number '" + number + "'") +
                ".";
            return false;
        }

        var cards = new List<SheetCard>(matches.Count);
        foreach (var match in matches)
        {
            cards.Add(new SheetCard
            {
                SheetId = match.Id.GetValue(),
                UniqueId = match.UniqueId,
                Number = match.SheetNumber ?? string.Empty,
                Name = match.Name
            });
        }

        candidates = cards;
        error = matches.Count + " sheets match the key; pass sheetId or the exact number.";
        return false;
    }
}