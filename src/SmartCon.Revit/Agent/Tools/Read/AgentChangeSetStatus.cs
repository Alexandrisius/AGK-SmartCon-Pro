using SmartCon.Core.Models.Agent;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Status derivation shared by the change-set read tools. The header keeps only
/// AppliedAt; rolled back and discarded sets differ by the recorded item status
/// (<see cref="ChangeStatus.RolledBack"/> vs <see cref="ChangeStatus.Discarded"/>).
/// </summary>
internal static class AgentChangeSetStatus
{
    public static string Derive(ChangeSetHeader header, IReadOnlyList<ChangeItem> items)
    {
        if (header.AppliedAt.HasValue)
            return "applied";

        foreach (var item in items)
        {
            if (item.Status == ChangeStatus.Discarded)
                return "discarded";
            if (item.Status == ChangeStatus.RolledBack)
                return "rolledBack";
        }

        return "closed";
    }
}
