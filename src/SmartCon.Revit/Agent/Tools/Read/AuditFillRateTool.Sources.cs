using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Source-collection half of audit_fill_rate: picking the source schedules
/// (on-sheet by default, key schedules skipped), deriving the unique parameter
/// identities from their fields and the single collector pass over the model
/// categories those schedules cover.
/// </summary>
public sealed partial class AuditFillRateTool
{
    private static bool SafeIsKeySchedule(ViewSchedule schedule)
    {
        try
        {
            return schedule.Definition.IsKeySchedule;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int CountFields(ViewSchedule schedule)
    {
        try
        {
            return schedule.Definition.GetFieldOrder().Count;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Unique parameter identities from schedule fields: shared by GUID, built-in
    /// by enum name; calculated fields (no parameter id) are skipped.
    /// </summary>
    private static void CollectParameters(
        Document doc,
        ViewSchedule schedule,
        Dictionary<string, AuditedParameter> parameters,
        HashSet<long> sourceCategories)
    {
        ScheduleDefinition definition;
        try
        {
            definition = schedule.Definition;
        }
        catch (Exception)
        {
            return;
        }

        try
        {
            var categoryId = definition.CategoryId;
            // Category ids of built-in categories are NEGATIVE (OST_Walls et al);
            // only the invalid id means "no category restriction".
            if (categoryId is not null && categoryId != ElementId.InvalidElementId)
                sourceCategories.Add(categoryId.GetValue());
        }
        catch (Exception)
        {
            // Multi-category schedules keep the element scope unrestricted.
        }

        IList<ScheduleFieldId> order;
        try
        {
            order = definition.GetFieldOrder();
        }
        catch (Exception)
        {
            return;
        }

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

            long parameterId;
            try
            {
                var raw = field.ParameterId;
                if (raw is null || raw == ElementId.InvalidElementId)
                    continue;

                parameterId = raw.GetValue();
            }
            catch (Exception)
            {
                // Calculated/combined fields have no parameter id.
                continue;
            }

            if (parameterId > 0)
            {
                var guid = AgentScheduleReader.DescribeParameterGuid(doc, field);
                if (guid is null)
                    continue;

                if (!parameters.ContainsKey(guid))
                {
                    var name = AgentScheduleReader.DescribeParameter(doc, field);
                    parameters.Add(guid, new AuditedParameter(
                        name ?? guid, guid, null, Guid.ParseExact(guid, "D")));
                }
            }
            else
            {
                var builtIn = (BuiltInParameter)(int)parameterId;
                var enumName = builtIn.ToString();
                if (parameters.ContainsKey(enumName))
                    continue;

                var label = AgentScheduleReader.DescribeParameter(doc, field) ?? enumName;
                parameters.Add(enumName, new AuditedParameter(label, null, enumName, null));
            }
        }
    }

    private static List<Element> CollectElements(Document doc, HashSet<long> categoryIds)
    {
        var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
        if (categoryIds.Count > 0)
        {
            // Several WherePasses calls combine as AND: multiple categories need
            // an explicit OR filter or the result is silently empty.
            var filters = new List<ElementFilter>(categoryIds.Count);
            foreach (var id in categoryIds)
                filters.Add(new ElementCategoryFilter(ElementIdCompat.Create(id)));
            collector.WherePasses(categoryIds.Count == 1
                ? filters[0]
                : new LogicalOrFilter(filters));
            return collector.ToElements().ToList();
        }

        // No category restriction (multi-category sources): keep it to model
        // elements by dropping the view/document infrastructure categories.
        collector.WherePasses(new ElementCategoryFilter(
            new ElementId(BuiltInCategory.OST_Views), inverted: true));
        collector.WherePasses(new ElementCategoryFilter(
            new ElementId(BuiltInCategory.OST_Sheets), inverted: true));
        collector.WherePasses(new ElementCategoryFilter(
            new ElementId(BuiltInCategory.OST_Schedules), inverted: true));
        collector.WherePasses(new ElementCategoryFilter(
            new ElementId(BuiltInCategory.OST_Viewports), inverted: true));
        return collector.ToElements().ToList();
    }
}
