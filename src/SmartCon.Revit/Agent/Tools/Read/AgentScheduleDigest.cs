using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Definition digest of one schedule: category, fields (order + hidden flags),
/// filters and sorting hashed to 12 hex chars. Two schedules with the same
/// digest show the same element set, so list_schedules can group duplicates the
/// way list_filters groups identical rule sets (rulesDigest, #452).
/// </summary>
internal static class AgentScheduleDigest
{
    internal static string Compute(ViewSchedule schedule)
    {
        try
        {
            ScheduleDefinition definition;
            try
            {
                definition = schedule.Definition;
            }
            catch (Exception)
            {
                return "unavailable";
            }

            var builder = new StringBuilder();
            AppendId(builder, "cat:", definition.CategoryId);

            var fields = new List<string>();
            foreach (var fieldId in definition.GetFieldOrder())
            {
                ScheduleField? field;
                try
                {
                    field = definition.GetField(fieldId);
                }
                catch (Exception)
                {
                    field = null;
                }

                // Parameter ids are stable across locales; display names are not.
                fields.Add(
                    (field is null ? fieldId.IntegerValue : ParameterKey(field)).ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                    + (field is not null && field.IsHidden ? ":h" : ":v"));
            }

            builder.Append("|fields:").Append(string.Join(",", fields));

            // The filter order does not change which elements pass (AND only),
            // so entries are sorted; the sort/group order does change the view.
            var filters = new List<string>();
            foreach (var filter in definition.GetFilters())
            {
                AgentScheduleWriteHelper.ReadFilterValue(
                    filter, out var valueString, out var valueNumber, out var valueElementId);
                var value = valueString is not null
                    ? "s:" + valueString
                    : valueNumber.HasValue
                        ? "n:" + valueNumber.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                        : valueElementId.HasValue
                            ? "e:" + valueElementId.Value
                            : "null";
                filters.Add(
                    (filter.FieldId?.IntegerValue ?? 0) + ":" + filter.FilterType + ":" + value);
            }

            filters.Sort(StringComparer.Ordinal);
            builder.Append("|filters:").Append(string.Join(",", filters));

            var sorts = new List<string>();
            foreach (var sort in definition.GetSortGroupFields())
                sorts.Add((sort.FieldId?.IntegerValue ?? 0) + ":" + sort.SortOrder);

            builder.Append("|sort:").Append(string.Join(",", sorts));
            builder.Append("|itemized:").Append(definition.IsItemized);
            builder.Append("|key:").Append(definition.IsKeySchedule);

            return Hash(builder.ToString());
        }
        catch (Exception)
        {
            return "unavailable";
        }
    }

    private static long ParameterKey(ScheduleField field)
    {
        try
        {
            var parameterId = field.ParameterId;
            if (parameterId is not null && parameterId != ElementId.InvalidElementId)
                return parameterId.GetValue();
        }
        catch (Exception)
        {
            // Calculated and combined fields have no parameter id.
        }

        try
        {
            return field.FieldId.IntegerValue;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static void AppendId(StringBuilder builder, string prefix, ElementId? id)
    {
        builder.Append(prefix);
        builder.Append(id is null || id == ElementId.InvalidElementId
            ? "-"
            : id.GetValue().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string Hash(string canonical)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        var hash = new StringBuilder(12);
        for (var index = 0; index < 6; index++)
            hash.Append(bytes[index].ToString("x2"));
        return hash.ToString();
    }
}
