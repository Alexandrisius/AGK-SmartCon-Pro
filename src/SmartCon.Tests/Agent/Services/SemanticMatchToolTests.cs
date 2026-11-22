using System.IO;
using System.Text;
using System.Text.Json;
using SmartCon.Agent.Services.Decisions;
using SmartCon.Agent.Services.Tools.Semantic;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class SemanticMatchToolTests
{
    private static JsonElement Parse(object payload) =>
        JsonSerializer.SerializeToElement(payload);

    private static AgentToolContext Context() =>
        new(new ImmediateAwaitableEvent(), "doc", Guid.Empty);

    private static SemanticMatchTool CreateTool(
        ScriptedDecisionClient client,
        AgentDecisionsSettings? settings = null) =>
        new(client, settings ?? new AgentDecisionsSettings());

    [Fact]
    public async Task Match_MergesBatchesRanksTopKAndEchoesModel()
    {
        // 260 items force two Choice batches: 255 + 5 (the router cap).
        var items = Enumerable.Range(1, 260)
            .Select(index => new { id = "id-" + index, text = "Строка " + index })
            .ToList();

        var client = new ScriptedDecisionClient
        {
            Answer = (state, questions) =>
            {
                var criteria = questions["match"].Criteria!;
                return criteria.ContainsKey("id-260")
                    ? ChoiceAnswer(new Dictionary<string, double> { ["id-260"] = 0.7 })
                    : ChoiceAnswer(new Dictionary<string, double>
                    {
                        ["id-3"] = 0.9,
                        ["id-1"] = 0.5,
                        ["id-2"] = 0.05
                    });
            }
        };
        var tool = CreateTool(client);

        var result = await tool.ExecuteAsync(
            Context(),
            Parse(new { items, query = "найди близкое", topK = 2 }),
            CancellationToken.None);

        Assert.True(result.Success);
        var data = result.Data!.Value;
        Assert.True(data.GetProperty("decisionUsed").GetBoolean());
        Assert.Equal("jev-test", data.GetProperty("model").GetString());
        Assert.Equal("typesafe", data.GetProperty("provider").GetString());
        Assert.Equal(2, data.GetProperty("batchesProcessed").GetInt32());
        Assert.Equal(260, data.GetProperty("candidatesTotal").GetInt32());

        var matches = data.GetProperty("matches").EnumerateArray().ToList();
        Assert.Equal(2, matches.Count);
        Assert.Equal("id-3", matches[0].GetProperty("id").GetString());
        Assert.Equal(0.9, matches[0].GetProperty("score").GetDouble(), 5);
        Assert.Equal(1, matches[0].GetProperty("rank").GetInt32());
        Assert.Equal("id-260", matches[1].GetProperty("id").GetString());
        Assert.Equal(2, matches[1].GetProperty("rank").GetInt32());

        // Two Choice questions: full 255 options, then the remainder of 5.
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(255, client.Requests[0].Questions["match"].Criteria!.Count);
        Assert.Equal(5, client.Requests[1].Questions["match"].Criteria!.Count);
    }

    [Fact]
    public async Task Match_FiltersCandidatesBelowMinScore()
    {
        var client = new ScriptedDecisionClient
        {
            Answer = (_, _) => ChoiceAnswer(new Dictionary<string, double>
            {
                ["a"] = 0.5,
                ["b"] = 0.05
            })
        };
        var tool = CreateTool(client);

        var result = await tool.ExecuteAsync(
            Context(),
            Parse(new
            {
                items = new[] { new { id = "a", text = "Alpha" }, new { id = "b", text = "Beta" } },
                query = "match alpha"
            }),
            CancellationToken.None);

        Assert.True(result.Success);
        var matches = result.Data!.Value.GetProperty("matches").EnumerateArray().ToList();
        var match = Assert.Single(matches);
        Assert.Equal("a", match.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Match_NothingAboveMinScore_WarnsInsteadOfGuessing()
    {
        var client = new ScriptedDecisionClient
        {
            Answer = (_, _) => ChoiceAnswer(new Dictionary<string, double> { ["a"] = 0.01 })
        };
        var tool = CreateTool(client);

        var result = await tool.ExecuteAsync(
            Context(),
            Parse(new { items = new[] { new { id = "a", text = "Alpha" } }, query = "q" }),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Value.GetProperty("matches").EnumerateArray());
        Assert.Contains(result.Warnings, warning => warning.Contains("minScore"));
    }

    [Fact]
    public async Task Match_ChoiceWithoutProbabilities_UsesThePickedOption()
    {
        var client = new ScriptedDecisionClient
        {
            Answer = (_, _) => new AgentDecisionAnswer
            {
                Type = "choice",
                Choice = "b",
                Probabilities = null
            }
        };
        var tool = CreateTool(client);

        var result = await tool.ExecuteAsync(
            Context(),
            Parse(new
            {
                items = new[] { new { id = "a", text = "Alpha" }, new { id = "b", text = "Beta" } },
                query = "match beta"
            }),
            CancellationToken.None);

        Assert.True(result.Success);
        var matches = result.Data!.Value.GetProperty("matches").EnumerateArray().ToList();
        var match = Assert.Single(matches);
        Assert.Equal("b", match.GetProperty("id").GetString());
        Assert.Equal(0.5, match.GetProperty("score").GetDouble(), 5);
    }

    [Fact]
    public async Task Match_TruncatesLongTextsTo500Chars()
    {
        var client = new ScriptedDecisionClient
        {
            Answer = (_, _) => ChoiceAnswer(new Dictionary<string, double> { ["x"] = 0.9 })
        };
        var tool = CreateTool(client);

        await tool.ExecuteAsync(
            Context(),
            Parse(new
            {
                items = new[] { new { id = "x", text = new string('д', 600) } },
                query = "q"
            }),
            CancellationToken.None);

        var criteria = client.Requests.Single().Questions["match"].Criteria!;
        Assert.Equal(500, criteria["x"]!.Length);
        Assert.EndsWith("...", criteria["x"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Match_DuplicateIds_KeepFirstAndWarn()
    {
        var client = new ScriptedDecisionClient
        {
            Answer = (_, _) => ChoiceAnswer(new Dictionary<string, double> { ["dup"] = 0.9 })
        };
        var tool = CreateTool(client);

        var result = await tool.ExecuteAsync(
            Context(),
            Parse(new
            {
                items = new[]
                {
                    new { id = "dup", text = "first" },
                    new { id = "dup", text = "second" }
                },
                query = "q"
            }),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.Data!.Value.GetProperty("candidatesTotal").GetInt32());
        Assert.Contains(result.Warnings, warning => warning.Contains("Duplicate"));
    }

    [Fact]
    public async Task Match_DisabledClient_ReturnsFailSoftWithHint()
    {
        var client = new ScriptedDecisionClient { Enabled = false };
        var tool = CreateTool(client);

        var result = await tool.ExecuteAsync(
            Context(),
            Parse(new { items = new[] { new { id = "x", text = "Alpha" } }, query = "q" }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("decision_unavailable", result.ErrorCode);
        var data = result.Data!.Value;
        Assert.False(data.GetProperty("decisionUsed").GetBoolean());
        Assert.Equal("decision_unavailable", data.GetProperty("reason").GetString());
        Assert.False(string.IsNullOrEmpty(data.GetProperty("hint").GetString()));
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Match_ClientFailure_ReturnsFailSoft()
    {
        var client = new ScriptedDecisionClient
        {
            Throw = new InvalidOperationException("decision model down")
        };
        var tool = CreateTool(client);

        var result = await tool.ExecuteAsync(
            Context(),
            Parse(new { items = new[] { new { id = "x", text = "Alpha" } }, query = "q" }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("decision_unavailable", result.ErrorCode);
        Assert.Equal("decision_unavailable", result.Data!.Value.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("items", "[]")]
    [InlineData("items", "null")]
    [InlineData("topK", "11")]
    [InlineData("topK", "0")]
    [InlineData("minScore", "1.5")]
    public async Task Match_InvalidArguments_AreRejected(string property, string value)
    {
        var client = new ScriptedDecisionClient
        {
            Answer = (_, _) => ChoiceAnswer(new Dictionary<string, double> { ["x"] = 0.9 })
        };
        var tool = CreateTool(client);

        var json = "{\"items\":[{\"id\":\"x\",\"text\":\"Alpha\"}],\"query\":\"q\",\"" +
                   property + "\":" + value + "}";
        using var document = JsonDocument.Parse(json);

        var result = await tool.ExecuteAsync(Context(), document.RootElement.Clone(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("invalid_arguments", result.ErrorCode);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Match_ItemWithoutText_IsRejected()
    {
        var client = new ScriptedDecisionClient
        {
            Answer = (_, _) => ChoiceAnswer(new Dictionary<string, double>())
        };
        var tool = CreateTool(client);

        using var document = JsonDocument.Parse("{\"items\":[{\"id\":\"x\"}],\"query\":\"q\"}");

        var result = await tool.ExecuteAsync(Context(), document.RootElement.Clone(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("invalid_arguments", result.ErrorCode);
    }

    [Theory]
    [InlineData("A30.", "A30.160|A30.190")]
    [InlineData("A10", "A10|A1010|A1010100")]
    [InlineData("30.20.10.", "30.20.10.10|30.20.10.20")]
    public async Task Match_CompactMode_CodePrefixFiltersLetterPrefixedAndDeepCodes(
        string prefix,
        string expectedCodes)
    {
        var content =
            "A10 Foundations\n" +
            "\tA1010 Standard Foundations\n" +
            "\t\tA1010100 Wall Foundations\n" +
            "A20 Basement Construction\n" +
            "\tA2010 Basement Excavation\n" +
            "A30.160 Кровля из металла\n" +
            "A30.190 Кровля штучная\n" +
            "A40.190 Вентиляция\n" +
            "30 Инженерные системы\n" +
            "\t30.20 Трубопроводная арматура\n" +
            "\t\t30.20.10 Краны шаровые\n" +
            "\t\t\t30.20.10.10 Краны шаровые муфтовые\n" +
            "\t\t\t30.20.10.20 Краны шаровые фланцевые\n" +
            "\t\t30.20.20 Задвижки\n";
        var expected = expectedCodes.Split('|');
        var client = new ScriptedDecisionClient
        {
            Answer = (_, _) => ChoiceAnswer(new Dictionary<string, double> { [expected[0]] = 0.9 })
        };
        var tool = CreateTool(client);

        var result = await WithClassifier(content, path => tool.ExecuteAsync(
            Context(),
            Parse(new { classifierPath = path, codePrefix = prefix, query = "подходящий код" }),
            CancellationToken.None));

        Assert.True(result.Success);
        Assert.Equal(expected.Length, result.Data!.Value.GetProperty("candidatesTotal").GetInt32());
        var criteria = client.Requests.Single().Questions["match"].Criteria!;
        Assert.Equal(expected.OrderBy(code => code), criteria.Keys.OrderBy(code => code));
    }

    [Fact]
    public async Task Match_CompactMode_LetterAndDeepCodes_CarryTheirBreadcrumbs()
    {
        var content =
            "A10 Foundations\n" +
            "\tA1010 Standard Foundations\n" +
            "30 Инженерные системы\n" +
            "\t30.20 Трубопроводная арматура\n" +
            "\t\t30.20.10 Краны шаровые\n" +
            "\t\t\t30.20.10.10 Краны шаровые муфтовые\n";
        var client = new ScriptedDecisionClient
        {
            Answer = (_, _) => ChoiceAnswer(new Dictionary<string, double> { ["30.20.10.10"] = 0.9 })
        };
        var tool = CreateTool(client);

        var result = await WithClassifier(content, path => tool.ExecuteAsync(
            Context(),
            Parse(new { classifierPath = path, query = "кран шаровой муфтовый" }),
            CancellationToken.None));

        Assert.True(result.Success);
        var criteria = client.Requests.Single().Questions["match"].Criteria!;
        Assert.Equal("Foundations › Standard Foundations", criteria["A1010"]);
        Assert.Equal(
            "Инженерные системы › Трубопроводная арматура › Краны шаровые › Краны шаровые муфтовые",
            criteria["30.20.10.10"]);
        var match = Assert.Single(result.Data!.Value.GetProperty("matches").EnumerateArray());
        Assert.Equal("30.20.10.10", match.GetProperty("id").GetString());
    }

    /// <summary>Writes the classifier to a temp file and removes it after the body ran.</summary>
    private static async Task<ToolResult> WithClassifier(string content, Func<string, Task<ToolResult>> body)
    {
        var path = Path.Combine(
            Path.GetTempPath(), "smartcon-semantic-classifier-" + Guid.NewGuid() + ".txt");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        try
        {
            return await body(path);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    private static AgentDecisionAnswer ChoiceAnswer(
        IReadOnlyDictionary<string, double> probabilities) =>
        new()
        {
            Type = "choice",
            Probabilities = probabilities
        };

    /// <summary>
    /// StubDecisionClient answers every request identically; batching tests
    /// need the answer to depend on the batch, hence a local scripted client.
    /// </summary>
    private sealed class ScriptedDecisionClient : IAgentDecisionClient
    {
        public Func<string, IReadOnlyDictionary<string, AgentDecisionQuestion>, AgentDecisionAnswer> Answer
        {
            get;
            set;
        } = (_, _) => new AgentDecisionAnswer { Type = "choice" };

        public bool Enabled { get; set; } = true;

        public Exception? Throw { get; set; }

        public List<(string State, IReadOnlyDictionary<string, AgentDecisionQuestion> Questions)> Requests
        {
            get;
        } = new();

        public string ProviderName => "typesafe";

        public string Model => "jev-test";

        public bool IsEnabled => Enabled;

        public Task<IReadOnlyDictionary<string, AgentDecisionAnswer>> AskAsync(
            string state,
            IReadOnlyDictionary<string, AgentDecisionQuestion> questions,
            CancellationToken ct)
        {
            if (Throw is not null)
                throw Throw;

            Requests.Add((state, questions));
            return Task.FromResult<IReadOnlyDictionary<string, AgentDecisionAnswer>>(
                new Dictionary<string, AgentDecisionAnswer>(StringComparer.Ordinal)
                {
                    ["match"] = Answer(state, questions)
                });
        }
    }
}
