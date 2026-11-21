using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Chain building for undo_until (G.10): given the headers of a document, the
/// applied sets from the newest down to (and including) the target set, in the
/// order they must be reverted. Rolled back and discarded sets are not applied,
/// so they are skipped automatically.
/// </summary>
public static class AgentUndoChain
{
    /// <summary>How many newest headers of a document the chain is built from.</summary>
    public const int Window = 100;

    public static (IReadOnlyList<ChangeSetHeader> Chain, string? ErrorCode, string? Message) Build(
        Guid targetId,
        IReadOnlyList<ChangeSetHeader> headers)
    {
        var applied = headers
            .Where(header => header.AppliedAt.HasValue)
            .OrderByDescending(header => header.AppliedAt)
            .ThenByDescending(header => header.CreatedAt)
            .ToList();

        var depth = 0;
        var found = false;
        foreach (var header in applied)
        {
            depth++;
            if (header.Id == targetId)
            {
                found = true;
                break;
            }
        }

        if (!found)
        {
            return (
                Array.Empty<ChangeSetHeader>(),
                "change_set_not_in_chain",
                "Change set " + targetId.ToString("D") +
                " is not part of the recent applied chain of this document (older than the " +
                "newest " + Window + " recorded sets).");
        }

        return (applied.Take(depth).ToList(), null, null);
    }
}
