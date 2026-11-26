using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>Schedule lookup used by the read and write tools.</summary>
internal static partial class AgentScheduleReader
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
    /// One schedule by id (preferred) or by exact name (case-insensitive). Used by
    /// the schedule read tools so all of them resolve the same way.
    /// </summary>
    internal static ViewSchedule? FindSchedule(Document doc, long scheduleId, string? scheduleName)
    {
        if (scheduleId > 0
            && doc.GetElement(ElementIdCompat.Create(scheduleId)) is ViewSchedule byId)
        {
            return byId;
        }

        if (string.IsNullOrWhiteSpace(scheduleName))
            return null;

        var trimmed = scheduleName!.Trim();
        foreach (var schedule in Collect(doc))
        {
            if (string.Equals(schedule.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                return schedule;
        }

        return null;
    }

    /// <summary>
    /// Categories of the document infrastructure that never supply rows to a
    /// schedule (the same exclusion audit_fill_rate applies to its scans).
    /// </summary>
}
