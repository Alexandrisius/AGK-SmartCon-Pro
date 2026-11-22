using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Tools;

public sealed partial class LoadToolPackTool
{
    /// <summary>
    /// Skill half of a <c>load_tool_pack</c> result. <see cref="Inline"/> names the
    /// packs whose manual this call delivers in <c>skills</c> (bounded by
    /// <see cref="MaxPacksPerCall"/>); <see cref="Available"/> names the packs of
    /// read-only tools, whose manual is only announced in <c>skillsAvailable</c>.
    /// </summary>
    private sealed class SkillDelivery
    {
        public List<string> Inline { get; } = new();

        public List<string> Available { get; } = new();

        public string? PackNote { get; set; }
    }

    /// <summary>
    /// Decides which manuals a call delivers. A skill is an operating manual for
    /// risky work: an explicitly requested pack always gets its manual, and a tool
    /// requested by name brings the manual of its pack only when it writes (risk
    /// Write or higher) — the same line the first write result follows (#400). A
    /// read-only tool needs no manual: loading one used to inline the whole manual
    /// of its pack, so a single lookup cost as much context as the riskiest call.
    /// Its pack is announced instead and <c>SentPackSkills</c> stays untouched.
    /// Explicit packs keep their slots and write-side owner packs fill the rest in
    /// name order, so the cap of <see cref="MaxPacksPerCall"/> picks
    /// deterministically (#453).
    /// </summary>
    private SkillDelivery ResolveSkillDelivery(
        IReadOnlyList<string> requestedPacks,
        IReadOnlyList<string> requestedTools,
        IToolRegistry registry)
    {
        var delivery = new SkillDelivery();
        foreach (var pack in requestedPacks)
            delivery.Inline.Add(pack);

        var writeOwners = new SortedSet<string>(StringComparer.Ordinal);
        var readOwners = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in requestedTools)
        {
            var owner = AgentToolPacks.PackOfTool(name)?.Name;
            if (owner is null
                || delivery.Inline.Contains(owner)
                || !AgentToolPackSkills.TryGet(owner, out _))
            {
                continue;
            }

            if (Writes(registry, name))
                writeOwners.Add(owner);
            else
                readOwners.Add(owner);
        }

        var totalSkillPacks = delivery.Inline.Count + writeOwners.Count;
        foreach (var owner in writeOwners)
        {
            if (delivery.Inline.Count < MaxPacksPerCall)
            {
                delivery.Inline.Add(owner);
                continue;
            }

            delivery.PackNote = "Skills attached for " + delivery.Inline.Count + " of " +
                                totalSkillPacks + " packs; call load_tool_pack for the rest.";
            break;
        }

        // One pack may own both a requested write tool and a requested read tool:
        // the write side wins, its manual is delivered and needs no announcement.
        foreach (var owner in readOwners)
        {
            if (!writeOwners.Contains(owner))
                delivery.Available.Add(owner);
        }

        return delivery;
    }

    private static bool Writes(IToolRegistry registry, string toolName) =>
        registry.TryGet(toolName, out var tool)
        && tool is not null
        && tool.Definition.Risk >= AgentToolRisk.Write;

    /// <summary>
    /// Announcement of the manuals that were not sent: every pack is named with a
    /// ready-to-copy request, so the model loads a manual right before it modifies
    /// that area instead of on every read-only lookup.
    /// </summary>
    private static string ComposeSkillHint(IReadOnlyList<string> packs) =>
        "Operating manual(s) available for pack(s) " + string.Join(", ", packs) +
        "; call " + string.Join(
            ", ",
            packs.Select(pack => "load_tool_pack(pack=\"" + pack + "\")")) +
        " before you choose or assign values with these tools (codes, mappings) or modify anything in that area — a plain read-only lookup needs no manual.";
}
