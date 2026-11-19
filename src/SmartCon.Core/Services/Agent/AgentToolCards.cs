using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Enriched retrieval cards for every agent tool (runbook §7.1, H1-06). The
/// table is data-only: no Revit API and no WPF. Card fields feed the catalog v2
/// router (H1-08); the legacy <see cref="AgentToolCatalog"/> fields stay intact.
/// </summary>
public static partial class AgentToolCards
{
    private static readonly AgentToolCard[] Cards;
    private static readonly Dictionary<string, AgentToolCard> CardsByName;

    static AgentToolCards()
    {
        Cards = ModelCards()
            .Concat(ViewCards())
            .Concat(ViewSettingsCards())
            .Concat(SheetCards())
            .Concat(ScheduleCards())
            .Concat(FilterCards())
            .Concat(FamilyCards())
            .Concat(AdminCards())
            .Concat(WriteCards())
            .Concat(JobCards())
            .ToArray();

        CardsByName = new Dictionary<string, AgentToolCard>(Cards.Length, StringComparer.Ordinal);
        foreach (var card in Cards)
            CardsByName.Add(card.ToolName, card);
    }

    /// <summary>All cards in deterministic order (model, views, view settings, write/meta).</summary>
    public static IReadOnlyList<AgentToolCard> All => Cards;

    public static int Count => Cards.Length;

    public static bool TryGet(string toolName, out AgentToolCard? card) =>
        CardsByName.TryGetValue(toolName, out card);
}
