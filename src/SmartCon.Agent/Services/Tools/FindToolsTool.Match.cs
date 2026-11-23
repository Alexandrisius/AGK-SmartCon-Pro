using System.Text.Json;
using SmartCon.Agent.Services.Decisions;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;

namespace SmartCon.Agent.Services.Tools;

public sealed partial class FindToolsTool
{
    /// <summary>
    /// Whether the router's own picks are a relevance verdict. Ranking by
    /// wording is not: the built-in BM25 router (also what the decision router
    /// falls back to while disabled) answers any query that shares one word
    /// with a card, so its picks must pass the query-coverage floor. A router
    /// with its own notion of relevance — the decision router while enabled, or
    /// a custom one — is not second-guessed: it can match a paraphrase that
    /// shares no word with the right card.
    /// </summary>
    private static bool RouterIsRelevanceAuthority(IToolRouter router) =>
        router switch
        {
            RuleToolRouter => false,
            DecisionsToolRouter decisions => decisions.IsEnabled,
            _ => true
        };

    /// <summary>
    /// Honest absence (C10-03): no card matches, so no card is returned. The
    /// nearest entries are named without their schemas and the packs are
    /// listed, so the model can either ask for one by its exact name or tell
    /// the user that the capability is missing.
    /// </summary>
    private static ToolResult NoMatchResult(IReadOnlyList<AgentToolDefinition> closest)
    {
        var nearest = closest.Take(5).ToList();
        var packs = nearest.Count > 0
            ? nearest.Select(PackOf)
                .Where(pack => !string.IsNullOrWhiteSpace(pack))
                .Select(pack => pack!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(pack => pack, StringComparer.Ordinal)
                .ToList()
            : AgentToolPacks.Populated.Select(pack => pack.Name).ToList();
        var payload = new
        {
            total = 0,
            noMatch = true,
            packs,
            closest = nearest
                .Select(definition => new { name = definition.Name, pack = PackOf(definition) })
                .ToList(),
            hint = nearest.Count > 0
                ? "No tool matches the query: the catalog entries that come close share only " +
                  "common wording with it. closest names them — query one by its exact name to " +
                  "see its card. If none of them does what you need, the capability is not in " +
                  "the catalog: say so and offer the closest supported alternative, never invent a tool."
                : "No tool matches the query: nothing in the catalog shares its wording. Describe " +
                  "the capability with a verb and an object (rename views, set parameter) or list " +
                  "the catalog with an empty query. If the capability is not in the catalog, say " +
                  "so and offer the closest supported alternative, never invent a tool.",
            tools = Array.Empty<object>()
        };

        return ToolResult.Ok(
            JsonSerializer.SerializeToElement(payload),
            "No tool matches in the catalog" +
            (packs.Count > 0 ? " (closest packs: " + string.Join(", ", packs) + ")." : "."));
    }

    /// <summary>
    /// Catalog tools whose full name the query spells out as a whole word
    /// ("get_view_template_details", "use get_view_settings for view 3").
    /// </summary>
    private static List<AgentToolDefinition> ExactNameMatches(
        string query,
        IReadOnlyList<AgentToolDefinition> candidates)
    {
        var matches = new List<AgentToolDefinition>();
        foreach (var definition in candidates)
        {
            if (ContainsToolName(query, definition.Name))
                matches.Add(definition);
        }

        return matches;
    }

    private static bool ContainsToolName(string text, string toolName)
    {
        var index = 0;
        while (index <= text.Length - toolName.Length)
        {
            index = text.IndexOf(toolName, index, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;

            var end = index + toolName.Length;
            if ((index == 0 || !IsNameCharacter(text[index - 1]))
                && (end == text.Length || !IsNameCharacter(text[end])))
            {
                return true;
            }

            index++;
        }

        return false;
    }

    private static bool IsNameCharacter(char character) =>
        char.IsLetterOrDigit(character) || character == '_';

    private static string? PackOf(AgentToolDefinition definition) =>
        !string.IsNullOrWhiteSpace(definition.Pack)
            ? definition.Pack
            : AgentToolPacks.PackOfTool(definition.Name)?.Name;
}
