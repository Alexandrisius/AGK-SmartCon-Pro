namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Plans a batch of renames so that no intermediate state violates the
/// uniqueness rule. Entries whose new name is currently held by another member
/// of the same batch are renamed to a temporary name first ("A→B, B→C" cycles).
/// </summary>
internal static class AgentRenamePlanner
{
    internal sealed class Entry
    {
        public long Id { get; init; }

        public required string CurrentName { get; init; }

        public required string NewName { get; init; }
    }

    internal static List<Entry> SelectTempRenames(IReadOnlyList<Entry> entries)
    {
        var changing = new List<Entry>();
        var currentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (string.Equals(entry.CurrentName, entry.NewName, StringComparison.Ordinal))
                continue;

            changing.Add(entry);
            currentNames.Add(entry.CurrentName);
        }

        var temp = new List<Entry>();
        foreach (var entry in changing)
        {
            if (currentNames.Contains(entry.NewName))
                temp.Add(entry);
        }

        return temp;
    }

    internal static string TempName() => "__sc_tmp_" + Guid.NewGuid().ToString("N").Substring(0, 12);
}
