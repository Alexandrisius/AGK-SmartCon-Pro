using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Revit.Agent.Tools.Read;

internal sealed class ScheduleColumn
{
    public int Index { get; set; }

    public string Heading { get; set; } = string.Empty;

    public string? ParameterName { get; set; }

    public bool IsHidden { get; set; }

    public string? FieldType { get; set; }
}

internal sealed class ScheduleCard
{
    public long ScheduleId { get; set; }

    public string UniqueId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Category { get; set; }

    public string? CategoryName { get; set; }

    public int FieldCount { get; set; }

    public int HiddenFieldCount { get; set; }

    public IReadOnlyList<string> Fields { get; set; } = Array.Empty<string>();

    public int RowCount { get; set; }

    public bool IsTemplate { get; set; }

    public bool IsKeySchedule { get; set; }

    public bool IsInternalKeynoteSchedule { get; set; }

    public bool IsTitleblockRevisionSchedule { get; set; }

    public IReadOnlyList<string> SheetNumbers { get; set; } = Array.Empty<string>();

    public bool PlacedOnSheet => SheetNumbers.Count > 0;

    /// <summary>Definition digest (fields/filters/sorting); "unavailable" when the definition cannot be read.</summary>
    public string? DefinitionsDigest { get; set; }
}

internal static class AgentScheduleReader
{
    internal static IReadOnlyList<ViewSchedule> Collect(Document doc)
    {
        var schedules = new List<ViewSchedule>();
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)))
        {
            if (element is ViewSchedule schedule)
                schedules.Add(schedule);
        }

        return schedules;
    }

    /// <summary>
    /// Categories of the document infrastructure that never supply rows to a
    /// schedule (the same exclusion audit_fill_rate applies to its scans).
    /// </summary>
    private static readonly BuiltInCategory[] InfrastructureCategories =
    {
        BuiltInCategory.OST_Views,
        BuiltInCategory.OST_Sheets,
        BuiltInCategory.OST_Schedules,
        BuiltInCategory.OST_Viewports
    };

    /// <summary>
    /// The single category of a schedule definition, or null: a multi-category
    /// schedule reports InvalidElementId here (Autodesk API reference) and no
    /// member of the API lists the categories of such a schedule.
    /// </summary>
    internal static ElementId? TryGetCategoryId(ScheduleDefinition definition)
    {
        try
        {
            var categoryId = definition.CategoryId;
            return categoryId is null || categoryId == ElementId.InvalidElementId
                ? null
                : categoryId;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static bool IsMultiCategory(ScheduleDefinition definition) =>
        TryGetCategoryId(definition) is null;

    /// <summary>
    /// The elements the rows of a schedule definition come from, at most
    /// <paramref name="cap"/> of them: the elements of its single category, or
    /// — for a multi-category schedule — every non-type element outside the
    /// document infrastructure. The wider set costs time only: an element
    /// that does not carry the parameter in question never passes a rule.
    /// </summary>
    internal static IEnumerable<Element> CollectSourceElements(
        Document doc,
        ScheduleDefinition definition,
        int cap)
    {
        FilteredElementCollector collector;
        var categoryId = TryGetCategoryId(definition);
        if (categoryId is not null)
        {
            var value = categoryId.GetValue();
            if (value < int.MinValue || value > int.MaxValue)
                yield break;

            collector = new FilteredElementCollector(doc)
                .OfCategory((BuiltInCategory)(int)value)
                .WhereElementIsNotElementType();
        }
        else
        {
            collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            foreach (var category in InfrastructureCategories)
            {
                collector.WherePasses(new ElementCategoryFilter(
                    new ElementId(category), inverted: true));
            }
        }

        var taken = 0;
        foreach (var element in collector)
        {
            yield return element;
            if (++taken >= cap)
                yield break;
        }
    }

    internal static IReadOnlyDictionary<long, IReadOnlyList<string>> MapSheetNumbers(Document doc)
    {
        var result = new Dictionary<long, List<string>>();
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)))
        {
            if (element is not ScheduleSheetInstance instance)
                continue;

            var owner = instance.OwnerViewId;
            if (owner is null || owner == ElementId.InvalidElementId)
                continue;

            var sheet = doc.GetElement(owner) as ViewSheet;
            if (sheet is null)
                continue;

            var key = instance.ScheduleId.GetValue();
            if (!result.TryGetValue(key, out var numbers))
            {
                numbers = new List<string>();
                result.Add(key, numbers);
            }

            var number = sheet.SheetNumber ?? string.Empty;
            if (!numbers.Contains(number))
                numbers.Add(number);
        }

        var final = new Dictionary<long, IReadOnlyList<string>>(result.Count);
        foreach (var pair in result)
            final.Add(pair.Key, pair.Value);
        return final;
    }

    internal static ScheduleCard CreateCard(
        Document doc,
        ViewSchedule schedule,
        IReadOnlyDictionary<long, IReadOnlyList<string>> sheetNumbers)
    {
        var hiddenFieldCount = 0;
        var columns = ReadColumns(doc, schedule, out hiddenFieldCount);
        var headings = new List<string>(columns.Count);
        foreach (var column in columns)
            headings.Add(column.Heading);

        var categoryId = ElementId.InvalidElementId;
        var isKeySchedule = false;
        try
        {
            categoryId = schedule.Definition.CategoryId;
            isKeySchedule = schedule.Definition.IsKeySchedule;
        }
        catch (Exception)
        {
            // Schedule templates have no table data; the definition is still valid.
        }

        var category = categoryId is null || categoryId == ElementId.InvalidElementId
            ? null
            : Category.GetCategory(doc, categoryId);
        var builtInCategory = category is null
            ? null
            : AgentElementCardFactory.GetCategoryName(category);

        sheetNumbers.TryGetValue(schedule.Id.GetValue(), out var onSheets);

        return new ScheduleCard
        {
            ScheduleId = schedule.Id.GetValue(),
            UniqueId = schedule.UniqueId,
            Name = schedule.Name,
            Category = builtInCategory,
            CategoryName = category?.Name,

            // C4-13: one meaning everywhere — fieldCount is the TOTAL number of
            // fields (visible + hidden), like audit_fill_rate's fieldCount;
            // fields[] lists only the visible headings.
            FieldCount = columns.Count + hiddenFieldCount,
            HiddenFieldCount = hiddenFieldCount,
            Fields = headings,
            RowCount = ReadRowCount(schedule, skipHeader: true),
            IsTemplate = schedule.IsTemplate,
            IsKeySchedule = isKeySchedule,
            IsInternalKeynoteSchedule = schedule.IsInternalKeynoteSchedule,
            IsTitleblockRevisionSchedule = schedule.IsTitleblockRevisionSchedule,
            SheetNumbers = onSheets ?? Array.Empty<string>(),
            DefinitionsDigest = AgentScheduleDigest.Compute(schedule)
        };
    }

    /// <summary>
    /// GUID of the shared parameter behind a schedule field, or null for
    /// built-in and calculated fields (shared ids are positive, built-in are
    /// negative, calculated have no parameter id — Autodesk developer guide).
    /// </summary>
    internal static string? DescribeParameterGuid(Document doc, ScheduleField field)
    {
        try
        {
            var parameterId = field.ParameterId;
            if (parameterId is null
                || parameterId == ElementId.InvalidElementId
                || parameterId.GetValue() <= 0)
            {
                return null;
            }

            return doc.GetElement(parameterId) is SharedParameterElement shared
                ? shared.GuidValue.ToString("D")
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static IReadOnlyList<ScheduleColumn> ReadColumns(
        Document doc,
        ViewSchedule schedule,
        out int hiddenFieldCount)
    {
        var columns = new List<ScheduleColumn>();
        hiddenFieldCount = 0;
        IList<ScheduleFieldId> order;
        ScheduleDefinition definition;
        try
        {
            definition = schedule.Definition;
            order = definition.GetFieldOrder();
        }
        catch (Exception)
        {
            return columns;
        }

        var index = 0;
        foreach (var fieldId in order)
        {
            ScheduleField? field;
            try
            {
                field = definition.GetField(fieldId);
            }
            catch (Exception)
            {
                continue;
            }

            if (field is null)
                continue;

            if (field.IsHidden)
            {
                hiddenFieldCount++;
                continue;
            }

            var heading = field.ColumnHeading;
            if (string.IsNullOrWhiteSpace(heading))
            {
                try
                {
                    heading = field.GetName();
                }
                catch (Exception)
                {
                    heading = string.Empty;
                }
            }

            columns.Add(new ScheduleColumn
            {
                Index = index,
                Heading = heading,
                ParameterName = DescribeParameter(doc, field),
                IsHidden = false,
                FieldType = field.FieldType.ToString()
            });
            index++;
        }

        return columns;
    }

    /// <summary>
    /// One page of body rows. Schedules may open with a title row and a heading
    /// row inside the body section; both are skipped when the heading row is
    /// recognised. Only the requested page is materialised, so a 100k-row
    /// schedule stays cheap.
    /// </summary>
    internal static (List<List<string>> Rows, bool HeaderSkipped, int Total) ReadBodyRows(
        ViewSchedule schedule,
        IReadOnlyList<ScheduleColumn> columns,
        int dataOffset,
        int limit)
    {
        var rows = new List<List<string>>();
        TableSectionData body;
        try
        {
            body = schedule.GetTableData().GetSectionData(SectionType.Body);
        }
        catch (Exception)
        {
            return (rows, false, 0);
        }

        var rowCount = body.NumberOfRows;
        var columnCount = body.NumberOfColumns;
        if (rowCount <= 0 || columnCount <= 0)
            return (rows, false, 0);

        var dataStart = FindDataStart(schedule, body, columns, rowCount, columnCount);
        var headerSkipped = dataStart > 0;
        var total = Math.Max(0, rowCount - dataStart);
        if (dataOffset >= total)
            return (rows, headerSkipped, total);

        var start = dataStart + dataOffset;
        var end = Math.Min(rowCount, start + Math.Max(1, limit));
        for (var row = start; row < end; row++)
        {
            var cells = new List<string>(columnCount);
            for (var column = 0; column < columnCount; column++)
                cells.Add(ReadCell(schedule, body, row, column));
            rows.Add(cells);
        }

        return (rows, headerSkipped, total);
    }

    internal static int ReadRowCount(ViewSchedule schedule, bool skipHeader)
    {
        try
        {
            var body = schedule.GetTableData().GetSectionData(SectionType.Body);
            var count = body.NumberOfRows;

            // A schedule without visible columns renders rows that cannot be read:
            // report zero data rows so list_schedules.rowCount matches
            // get_schedule_data.totalRows (a corrupted schedule used to report 106
            // rows here and 0 rows there).
            if (count <= 0 || body.NumberOfColumns <= 0)
                return 0;

            if (!skipHeader)
                return Math.Max(0, count);

            var columns = ReadColumns(schedule.Document, schedule, out _);
            var dataStart = FindDataStart(
                schedule, body, columns, count, body.NumberOfColumns);
            return Math.Max(0, count - dataStart);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Index of the first data row: past a recognised heading row and, when the
    /// schedule shows a title, past the title row that Revit renders blank inside
    /// the body section.
    /// </summary>
    private static int FindDataStart(
        ViewSchedule schedule,
        TableSectionData body,
        IReadOnlyList<ScheduleColumn> columns,
        int rowCount,
        int columnCount)
    {
        var probeRows = Math.Min(5, rowCount);
        for (var row = 0; row < probeRows; row++)
        {
            var cells = ReadRow(schedule, body, row, columnCount);
            if (!LooksLikeHeader(cells, columns))
                continue;

            var start = row + 1;
            if (start < rowCount && ShowsTitle(schedule) && IsBlankRow(schedule, body, start, columnCount))
                start++;
            return start;
        }

        return 0;
    }

    private static bool ShowsTitle(ViewSchedule schedule)
    {
        try
        {
            return schedule.Definition.ShowTitle;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static List<string> ReadRow(
        ViewSchedule schedule,
        TableSectionData body,
        int row,
        int columnCount)
    {
        var cells = new List<string>(columnCount);
        for (var column = 0; column < columnCount; column++)
            cells.Add(ReadCell(schedule, body, row, column));
        return cells;
    }

    private static bool IsBlankRow(
        ViewSchedule schedule,
        TableSectionData body,
        int row,
        int columnCount)
    {
        foreach (var cell in ReadRow(schedule, body, row, columnCount))
        {
            if (!string.IsNullOrWhiteSpace(cell))
                return false;
        }

        return true;
    }

    private static string ReadCell(
        ViewSchedule schedule,
        TableSectionData body,
        int row,
        int column)
    {
        // TableView.GetCellText returns the formatted text for standard
        // schedules; TableSectionData.GetCellText keeps plain header cells that
        // the formatted reader reports as empty.
        var formatted = string.Empty;
        try
        {
            formatted = schedule.GetCellText(SectionType.Body, row, column) ?? string.Empty;
        }
        catch (Exception)
        {
            // Out-of-range rows and tables without a body section.
        }

        if (!string.IsNullOrWhiteSpace(formatted))
            return formatted;

        try
        {
            return body.GetCellText(row, column) ?? string.Empty;
        }
        catch (Exception)
        {
            // Merged or non-text cells (graphic cells) have no text.
        }

        return string.Empty;
    }

    private static bool LooksLikeHeader(
        IReadOnlyList<string> firstRow,
        IReadOnlyList<ScheduleColumn> columns)
    {
        if (firstRow.Count == 0 || columns.Count == 0)
            return false;

        var compared = 0;
        var matched = 0;
        for (var index = 0; index < firstRow.Count && index < columns.Count; index++)
        {
            var heading = Normalize(columns[index].Heading);
            var cell = Normalize(firstRow[index]);
            if (heading.Length == 0 || cell.Length == 0)
                continue;

            compared++;
            if (cell == heading || cell.IndexOf(heading, StringComparison.Ordinal) >= 0
                || heading.IndexOf(cell, StringComparison.Ordinal) >= 0)
            {
                matched++;
            }
        }

        return compared > 0 && matched * 10 >= compared * 6;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value!.Trim().ToLowerInvariant().Replace("\n", " ").Replace("  ", " ");
    }

    internal static string? DescribeParameter(Document doc, ScheduleField field)
    {
        try
        {
            var parameterId = field.ParameterId;
            if (parameterId is null || parameterId == ElementId.InvalidElementId)
                return null;

            var value = parameterId.GetValue();
            if (value < 0)
            {
                try
                {
                    return LabelUtils.GetLabelFor((BuiltInParameter)value);
                }
                catch (Exception)
                {
                    return null;
                }
            }

            var element = doc.GetElement(ElementIdCompat.Create(value));
            return element?.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }
}