using System.Globalization;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Revit.Agent;

namespace SmartCon.Revit.Agent.Tools.Read;

internal sealed class SheetCard
{
    public long SheetId { get; set; }

    public string UniqueId { get; set; } = string.Empty;

    public string Number { get; set; } = string.Empty;

    /// <summary>Number with invisible code units removed and spaces collapsed.</summary>
    public string NumberVisual { get; set; } = string.Empty;

    public bool HasInvisibleCharacters { get; set; }

    /// <summary>Exact code points, present only when the number is not plain text.</summary>
    public IReadOnlyList<int>? NumberCodePoints { get; set; }

    /// <summary>Another sheet has the same visual number but a different raw number.</summary>
    public bool DuplicateVisualNumber { get; set; }

    public string Name { get; set; } = string.Empty;

    public double? WidthMm { get; set; }

    public double? HeightMm { get; set; }

    public int ViewCount { get; set; }

    /// <summary>Placed schedules that carry content (title-block revision schedules are excluded).</summary>
    public int ScheduleCount { get; set; }

    /// <summary>Title-block revision schedules: present on every sheet, not sheet content.</summary>
    public int RevisionScheduleCount { get; set; }

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

    /// <summary>
    /// Placed schedules per sheet. Title-block revision schedules are counted
    /// separately: every sheet with a title block has at least one, so counting
    /// them as content made an empty sheet look filled.
    /// </summary>
    internal static IReadOnlyDictionary<long, ScheduleCounts> CountPlacedSchedules(Document doc)
    {
        var counts = new Dictionary<long, ScheduleCounts>();
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)))
        {
            if (element is not ScheduleSheetInstance instance)
                continue;

            var owner = instance.OwnerViewId;
            if (owner is null || owner == ElementId.InvalidElementId)
                continue;

            var key = owner.GetValue();
            counts.TryGetValue(key, out var current);
            if (instance.IsTitleblockRevisionSchedule)
                current.Revisions++;
            else
                current.Schedules++;
            counts[key] = current;
        }

        return counts;
    }

    internal struct ScheduleCounts
    {
        public int Schedules { get; set; }

        public int Revisions { get; set; }
    }

    /// <summary>
    /// Visual keys shared by more than one sheet. Revit allows two sheet numbers
    /// that differ only by an invisible code unit; the model must never see them
    /// as the same value (stress test: "\uFFFD1" for two different sheets).
    /// </summary>
    internal static ISet<string> FindVisualDuplicateNumbers(IReadOnlyList<ViewSheet> sheets)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in sheets)
        {
            var visual = AgentTextAudit.Visual(sheet.SheetNumber);
            counts.TryGetValue(visual, out var current);
            counts[visual] = current + 1;
        }

        var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in counts)
        {
            if (pair.Value > 1)
                duplicates.Add(pair.Key);
        }

        return duplicates;
    }

    internal static SheetCard CreateCard(
        ViewSheet sheet,
        IReadOnlyDictionary<long, ScheduleCounts> scheduleCounts,
        ISet<string>? visualDuplicates = null)
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
        var number = sheet.SheetNumber ?? string.Empty;
        var visual = AgentTextAudit.Visual(number);

        return new SheetCard
        {
            SheetId = sheetId,
            UniqueId = sheet.UniqueId,
            Number = number,
            NumberVisual = visual,
            HasInvisibleCharacters = AgentTextAudit.HasInvisibleCharacters(number),
            NumberCodePoints = AgentTextAudit.HasInvisibleCharacters(number)
                ? AgentTextAudit.CodePoints(number)
                : null,
            DuplicateVisualNumber = visualDuplicates?.Contains(visual) == true,
            Name = sheet.Name,
            WidthMm = widthMm,
            HeightMm = heightMm,
            ViewCount = sheet.GetAllPlacedViews().Count,
            ScheduleCount = scheduleCount.Schedules,
            RevisionScheduleCount = scheduleCount.Revisions,
            IsPlaceholder = sheet.IsPlaceholder
        };
    }

    /// <summary>
    /// Splits a sheet number into the leading prefix and the trailing number
    /// ("ОВ-07" → prefix "ОВ-", value 7, digits 2). Numbers without trailing
    /// digits return false.
    /// </summary>
    internal static bool TrySplitTrailingNumber(
        string number,
        out string prefix,
        out int value,
        out int digits)
    {
        prefix = string.Empty;
        value = 0;
        digits = 0;
        if (string.IsNullOrEmpty(number))
            return false;

        var start = number!.Length;
        while (start > 0 && char.IsDigit(number[start - 1]))
            start--;

        digits = number.Length - start;
        if (digits == 0)
            return false;

        if (!int.TryParse(
                number.Substring(start, digits),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value))
            return false;

        prefix = number[..start];
        return true;
    }

    /// <summary>
    /// The tail of the album numbering: the prefix and zero padding of the sheet
    /// with the highest trailing number, plus that number ("ОВ-3" → "ОВ-", 3, 1;
    /// sheets 1..3 → "", 3, 1). False when no sheet carries a trailing number.
    /// </summary>
    internal static bool TryGetAlbumTail(
        Document doc,
        out string prefix,
        out int maxValue,
        out int digits)
    {
        prefix = string.Empty;
        maxValue = 0;
        digits = 1;
        var found = false;
        foreach (var sheet in Collect(doc, excludePlaceholders: false))
        {
            if (!TrySplitTrailingNumber(
                    sheet.SheetNumber ?? string.Empty, out var sheetPrefix, out var value, out var sheetDigits))
                continue;

            if (found && value <= maxValue)
                continue;

            prefix = sheetPrefix;
            maxValue = value;
            digits = sheetDigits;
            found = true;
        }

        return found;
    }

    /// <summary>
    /// Formats a continued album number, keeping the zero padding of the tail
    /// sheet ("ОВ-07" + 8 → "ОВ-08"; padding drops once the value outgrows it).
    /// </summary>
    internal static string FormatContinuedNumber(string prefix, int value, int digits)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        if (digits > text.Length)
        {
            text = value.ToString(
                "D" + digits.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture);
        }

        return prefix + text;
    }

    /// <summary>
    /// The sheet number that continues the album (build_system_sheets uses the
    /// same algorithm when an item carries no sheetNumber).
    /// </summary>
    internal static string NextNumberHint(Document doc) =>
        TryGetAlbumTail(doc, out var prefix, out var maxValue, out var digits)
            ? FormatContinuedNumber(prefix, maxValue + 1, digits)
            : "1";

    /// <summary>
    /// Finds exactly one sheet by id, number or name. Returns false with a
    /// non-null error when nothing matched, and fills <paramref name="candidates"/>
    /// when the key is ambiguous. Number matching distinguishes the raw value
    /// from the visible glyphs: a query that would silently pick one of several
    /// visually identical numbers returns all candidates instead.
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
        if (!string.IsNullOrWhiteSpace(number))
        {
            var rawMatches = new List<ViewSheet>();
            var visualMatches = new List<ViewSheet>();
            var visualKey = AgentTextAudit.Visual(number);
            foreach (var candidate in all)
            {
                if (string.Equals(candidate.SheetNumber, number, StringComparison.OrdinalIgnoreCase))
                    rawMatches.Add(candidate);

                if (string.Equals(
                        AgentTextAudit.Visual(candidate.SheetNumber),
                        visualKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    visualMatches.Add(candidate);
                }
            }

            if (rawMatches.Count == 1 && visualMatches.Count == 1)
            {
                sheet = rawMatches[0];
                return true;
            }

            if (rawMatches.Count > 1)
            {
                candidates = ToCandidates(rawMatches);
                error = rawMatches.Count + " sheets match the number '" + number +
                        "'; pass sheetId.";
                return false;
            }

            if (visualMatches.Count == 1)
            {
                // Exactly one sheet is visually identical; its card carries
                // hasInvisibleCharacters/numberCodePoints so the caller can see
                // that the raw number is not plain text.
                sheet = visualMatches[0];
                return true;
            }

            if (visualMatches.Count > 1)
            {
                candidates = ToCandidates(visualMatches);
                error = visualMatches.Count + " sheets look like number '" + number +
                        "' after removing invisible characters; their raw numbers differ. " +
                        "Pass sheetId (the candidates list shows numberCodePoints).";
                return false;
            }

            error = "No sheet matches number '" + number + "'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Pass sheetId, number or name to identify the sheet.";
            return false;
        }

        var matches = new List<ViewSheet>();
        foreach (var candidate in all)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                matches.Add(candidate);
        }

        if (matches.Count == 1)
        {
            sheet = matches[0];
            return true;
        }

        if (matches.Count == 0)
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
            error = "No sheet matches name '" + name + "'.";
            return false;
        }

        candidates = ToCandidates(matches);
        error = matches.Count + " sheets match the name '" + name + "'; pass sheetId.";
        return false;
    }

    private static IReadOnlyList<SheetCard> ToCandidates(IReadOnlyList<ViewSheet> sheets)
    {
        var cards = new List<SheetCard>(sheets.Count);
        foreach (var sheet in sheets)
        {
            var number = sheet.SheetNumber ?? string.Empty;
            cards.Add(new SheetCard
            {
                SheetId = sheet.Id.GetValue(),
                UniqueId = sheet.UniqueId,
                Number = number,
                NumberVisual = AgentTextAudit.Visual(number),
                HasInvisibleCharacters = AgentTextAudit.HasInvisibleCharacters(number),
                NumberCodePoints = AgentTextAudit.HasInvisibleCharacters(number)
                    ? AgentTextAudit.CodePoints(number)
                    : null,
                Name = sheet.Name
            });
        }

        return cards;
    }
}