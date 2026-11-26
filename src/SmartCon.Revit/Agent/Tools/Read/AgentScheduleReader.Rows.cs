using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>Table body reading: rows, cells, header detection.</summary>
internal static partial class AgentScheduleReader
{
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
