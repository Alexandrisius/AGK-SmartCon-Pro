using System.Globalization;
using System.IO;
using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;

namespace SmartCon.Tests.Agent.Routing;

/// <summary>Routing eval fixture row (H1-11).</summary>
internal sealed class RoutingQueryFixture
{
    public string Id { get; init; } = string.Empty;

    public string Query { get; init; } = string.Empty;

    public List<string> ExpectedTools { get; init; } = new();

    public string Kind { get; init; } = "read";

    public string? Token { get; init; }
}

/// <summary>
/// Builds the two catalogs used by the H1-11/H1-12 routing eval: the real
/// 27-card catalog and a deterministic 1000-card synthetic catalog. The
/// synthetic card tokens are shared with <c>synthetic-queries.json</c>; the
/// formula must stay in sync with that fixture generator.
/// </summary>
internal static class RoutingCatalogFactory
{
    public const int Packs = 20;
    public const int CardsPerPack = 50;
    public const int SyntheticCardCount = Packs * CardsPerPack;

    private const string DummySchema = """{"type":"object","properties":{}}""";

    public static IReadOnlyList<AgentToolDefinition> RealCatalog() =>
        AgentToolCards.All
            .Select(card => AgentToolCatalog.Enrich(new AgentToolDefinition
            {
                Name = card.ToolName,
                Description = card.Purpose,
                InputSchemaJson = DummySchema
            }))
            .ToList();

    public static IReadOnlyList<AgentToolDefinition> SyntheticCatalog()
    {
        var catalog = new List<AgentToolDefinition>(SyntheticCardCount);
        foreach (var pack in AgentToolPacks.All)
        {
            for (var index = 1; index <= CardsPerPack; index++)
            {
                var token = SyntheticToken(pack.Name, index);
                var name = SyntheticName(pack.Name, index);
                catalog.Add(new AgentToolDefinition
                {
                    Name = name,
                    Description = token + ": синтетическая операция пака " + pack.Name + ".",
                    Risk = AgentToolRisk.Read,
                    InputSchemaJson = DummySchema,
                    Category = "Read",
                    Pack = pack.Name,
                    Purpose = token + ": операция " + index.ToString(CultureInfo.InvariantCulture) +
                              " пака " + pack.Name + " для BIM-сценария.",
                    WhenToUse = "Когда нужен сценарий " + token + " и пак " + pack.Name + ".",
                    WhenNotToUse = "Когда нужен сценарий другого пака.",
                    Examples = new[]
                    {
                        token + " на плане",
                        "Выполни операцию " + pack.Name + " номер " +
                        index.ToString(CultureInfo.InvariantCulture)
                    },
                    Synonyms = new[] { token, pack.Name, "сценарий " + index.ToString(CultureInfo.InvariantCulture) },
                    NegativeExamples = Array.Empty<string>()
                });
            }
        }

        return catalog;
    }

    public static string SyntheticToken(string pack, int index) =>
        "zzq" + pack.Replace("_", string.Empty) + index.ToString("D3", CultureInfo.InvariantCulture);

    public static string SyntheticName(string pack, int index) =>
        pack + "_" + index.ToString("D2", CultureInfo.InvariantCulture);

    /// <summary>
    /// Representative tool schema (~800 chars): the production schemas live in
    /// SmartCon.Agent, so the prompt-size eval uses a schema of the same shape
    /// and size instead of claiming to measure the real ones.
    /// </summary>
    public static string RepresentativeSchema() =>
        """
        {"type":"object","properties":{
          "items":{"type":"array","minItems":1,"maxItems":200,
            "items":{"type":"object","properties":{
              "elementId":{"type":"integer"},
              "viewId":{"type":"integer"},
              "newName":{"type":"string"},
              "templateId":{"type":"integer"},
              "parameterName":{"type":"string"},
              "value":{"type":"string"},
              "unit":{"type":"string","enum":["mm","m","deg"]},
              "dryRun":{"type":"boolean","default":true}},
            "required":["elementId"],"additionalProperties":false}},
          "fields":{"type":"array","items":{"type":"string"}},
          "limit":{"type":"integer","minimum":1,"maximum":200,"default":50},
          "offset":{"type":"integer","minimum":0,"default":0},
          "dryRun":{"type":"boolean","default":true}},
        "required":["items"],"additionalProperties":false}
        """;
}

/// <summary>Loads the routing eval fixtures from the test output directory.</summary>
internal static class RoutingFixtures
{
    public static IReadOnlyList<RoutingQueryFixture> RealQueries() =>
        Load("real-queries.json");

    public static IReadOnlyList<RoutingQueryFixture> SyntheticQueries() =>
        Load("synthetic-queries.json");

    private static IReadOnlyList<RoutingQueryFixture> Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Agent", "Routing", fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException("Routing fixture not found: " + path, path);

        var root = JsonSerializer.Deserialize<FixtureRoot>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (root?.Queries is null || root.Queries.Count == 0)
            throw new InvalidOperationException("Routing fixture has no queries: " + path);

        return root.Queries;
    }

    private sealed class FixtureRoot
    {
        public List<RoutingQueryFixture>? Queries { get; init; }
    }
}
