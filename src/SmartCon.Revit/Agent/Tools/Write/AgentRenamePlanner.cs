namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Plans a batch of renames so that no intermediate state violates the
/// uniqueness rule: names released by the batch are treated as free, chains are
/// ordered head-first and cycles ("A→B, B→A") are broken with a temporary name.
/// </summary>
internal static class AgentRenamePlanner
{
    internal sealed class Entry
    {
        public long Id { get; init; }

        public required string CurrentName { get; init; }

        public required string NewName { get; init; }
    }

    internal sealed class Plan
    {
        /// <summary>Entries to rename, in an order that never collides.</summary>
        public List<Entry> Order { get; } = new();

        /// <summary>Entries that must be moved to a temporary name before <see cref="Order"/>.</summary>
        public List<Entry> TempFirst { get; } = new();
    }

    /// <summary>
    /// Builds the execution plan for a validated batch.
    /// </summary>
    /// <param name="entries">Validated entries with unique ids and target names.</param>
    /// <param name="documentNames">All names currently used in the document.</param>
    internal static Plan Build(IReadOnlyList<Entry> entries, IEnumerable<string> documentNames)
    {
        var changing = entries
            .Where(entry => !string.Equals(entry.CurrentName, entry.NewName, StringComparison.Ordinal))
            .ToList();

        var heldByBatch = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in changing)
            heldByBatch[entry.CurrentName] = entry.Id;

        // Names owned by elements outside the batch never become free during the batch.
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in documentNames)
        {
            if (!heldByBatch.ContainsKey(name))
                blocked.Add(name);
        }

        var plan = new Plan();
        var pending = new List<Entry>(changing);
        while (pending.Count > 0)
        {
            Entry? next = null;
            foreach (var entry in pending)
            {
                if (blocked.Contains(entry.NewName) || heldByBatch.ContainsKey(entry.NewName))
                    continue;

                next = entry;
                break;
            }

            if (next is null)
            {
                // Every remaining entry points at a name still held inside the batch: a
                // cycle. Moving one entry to a temporary name releases its current name
                // and breaks the deadlock. Entries whose target is blocked by an element
                // outside the batch cannot be planned at all (the caller validates them).
                next = pending.FirstOrDefault(entry =>
                    !plan.TempFirst.Contains(entry) && heldByBatch.ContainsKey(entry.NewName));
                if (next is null)
                    break;

                plan.TempFirst.Add(next);
                heldByBatch.Remove(next.CurrentName);
                continue;
            }

            plan.Order.Add(next);
            pending.Remove(next);
            heldByBatch.Remove(next.CurrentName);
            blocked.Add(next.NewName);
        }

        return plan;
    }

    internal const string TempPrefix = "__sc_tmp_";

    internal static string TempName() =>
        TempPrefix + Guid.NewGuid().ToString("N").Substring(0, 12);
}
