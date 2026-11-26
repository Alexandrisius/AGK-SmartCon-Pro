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

    /// <summary>Across categories: CategoryId is InvalidElementId, so category and categoryName are null.</summary>
    public bool IsMulticategory { get; set; }

    public bool IsInternalKeynoteSchedule { get; set; }

    public bool IsTitleblockRevisionSchedule { get; set; }

    public IReadOnlyList<string> SheetNumbers { get; set; } = Array.Empty<string>();

    public bool PlacedOnSheet => SheetNumbers.Count > 0;

    /// <summary>Definition digest (fields/filters/sorting); "unavailable" when the definition cannot be read.</summary>
    public string? DefinitionsDigest { get; set; }
}

internal static partial class AgentScheduleReader
{
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
        var isMulticategory = false;
        try
        {
            categoryId = schedule.Definition.CategoryId;
            isKeySchedule = schedule.Definition.IsKeySchedule;
            isMulticategory = categoryId is null || categoryId == ElementId.InvalidElementId;
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
            IsMulticategory = isMulticategory,
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
}
