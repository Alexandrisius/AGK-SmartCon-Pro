using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Classification;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// The per-type differentiation stage of classify_types against the real
/// template (C5-04): a family with ONE code and types whose names differ is
/// asked per type; a scripted sibling answer for one type splits the family in
/// assignments and setParameters. Runs on whatever wall types the template
/// carries - when their names do not differ, the stage must stay silent.
/// Partial of the test class: reuses its seed and helpers.
/// </summary>
public sealed partial class AgentClassifyTypesTests
{
    private const string NoneId = "__none__";

    /// <summary>Classify with an arbitrary decision client (the shared helper takes its own).</summary>
    private Task<ToolResult> ClassifyWithAsync(object args, IAgentDecisionClient client) =>
        new ClassifyTypesTool(
                new StubRevitContext(Doc),
                new AgentSemanticRanker(client))
            .ExecuteForTestsAsync(Doc, Args(args), CancellationToken.None);

    [Test]
    public async Task Classify_SingleCodeFamily_PerTypeSiblingSplitsAssignments()
    {
        RequireWallTypes(2);
        await Assert.That(LoadSampleTable("classify-typestage.txt").Success).IsTrue();

        var client = new TypeStageScriptClient();
        var result = await ClassifyWithAsync(new { categories = new[] { "OST_Walls" } }, client);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;

        if (!WallTypeNamesDiffer())
        {
            // Honest degradation: nothing to ask the model about.
            await Assert.That(client.PerTypeQuestions).IsEqualTo(0);
            return;
        }

        await Assert.That(client.PerTypeQuestions).IsGreaterThanOrEqualTo(2);

        // The scripted sibling answer became a proposal of its own group.
        var sibling = client.SiblingAnswer!;
        await Assert.That(sibling).IsNotEqualTo(NoneId);
        var assignments = data.GetProperty("assignments").EnumerateArray().ToList();
        var siblingGroup = assignments.FirstOrDefault(group =>
            group.GetProperty("code").GetString() == sibling);
        await Assert.That(siblingGroup.ValueKind).IsEqualTo(JsonValueKind.Object);
        var siblingTypeIds = siblingGroup.GetProperty("typeIds").EnumerateArray()
            .Select(id => id.GetInt64())
            .ToList();
        await Assert.That(siblingTypeIds.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(siblingTypeIds.All(_wallTypeIds.Contains)).IsTrue();

        // The ids assigned by setParameters are exactly the assigned type ids,
        // and the sibling value really travels into the write payload.
        var items = data.GetProperty("setParameters").GetProperty("items").EnumerateArray()
            .ToList();
        var assignedTypeIds = assignments
            .SelectMany(group => group.GetProperty("typeIds").EnumerateArray())
            .Select(id => id.GetInt64())
            .ToHashSet();
        var itemIds = items.Select(item => item.GetProperty("elementId").GetInt64()).ToHashSet();
        await Assert.That(assignedTypeIds.SetEquals(itemIds)).IsTrue();
        await Assert.That(items.Any(item =>
            item.GetProperty("value").GetString() == sibling
            && siblingTypeIds.Contains(item.GetProperty("elementId").GetInt64()))).IsTrue();

        // The per-type stage made real decision calls and they are reported.
        var decisionCalls = data.GetProperty("summary").GetProperty("decisionCalls").GetInt32();
        var perTypeCalls = (client.PerTypeQuestions + 19) / 20;
        await Assert.That(decisionCalls).IsGreaterThanOrEqualTo(perTypeCalls);
    }

    [Test]
    public async Task Classify_SingleCodeFamily_PerTypeAbstainKeepsFamilyCode()
    {
        RequireWallTypes(2);
        await Assert.That(LoadSampleTable("classify-typestage-abstain.txt").Success).IsTrue();

        var client = new TypeStageScriptClient { AbstainPerType = true };
        var result = await ClassifyWithAsync(new { categories = new[] { "OST_Walls" } }, client);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        if (!WallTypeNamesDiffer())
        {
            await Assert.That(client.PerTypeQuestions).IsEqualTo(0);
            return;
        }

        await Assert.That(client.PerTypeQuestions).IsGreaterThanOrEqualTo(2);
        // An honest none per type keeps the family code: no type is lost.
        var assignments = data.GetProperty("assignments").EnumerateArray().ToList();
        await Assert.That(assignments.Count).IsGreaterThanOrEqualTo(1);
        var assignedTypeIds = assignments
            .SelectMany(group => group.GetProperty("typeIds").EnumerateArray())
            .Select(id => id.GetInt64())
            .ToHashSet();
        await Assert.That(assignedTypeIds.SetEquals(_wallTypeIds.ToHashSet())).IsTrue();
    }

    /// <summary>
    /// The tool's significance predicate, recomputed here (the matcher's private
    /// original is not callable): the wall type names must tokenize into at
    /// least two different token sets after the family-name tokens are removed.
    /// </summary>
    private bool WallTypeNamesDiffer()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in _wallTypeIds)
        {
            var type = Doc.GetElement(ElementIdCompat.Create(id)) as WallType;
            if (type is null)
                continue;

            var tokens = TokensOf(type.Name);
            foreach (var familyToken in TokensOf(type.FamilyName ?? string.Empty))
                tokens.Remove(familyToken);
            keys.Add(string.Join("|", tokens.OrderBy(token => token, StringComparer.Ordinal)));
        }

        return keys.Count >= 2;
    }

    private static HashSet<string> TokensOf(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var builder = new StringBuilder();
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                continue;
            }

            Flush(builder, tokens);
        }

        Flush(builder, tokens);
        return tokens;
    }

    private static void Flush(StringBuilder builder, HashSet<string> tokens)
    {
        if (builder.Length > 0)
        {
            var token = builder.ToString().ToLowerInvariant();
            // Pure numbers are not meaningful differences (the matcher drops them).
            if (!token.All(char.IsDigit))
                tokens.Add(token);
        }

        builder.Clear();
    }

    /// <summary>
    /// The keyword decision client with a scripted differentiation stage: the
    /// FIRST per-type question gets the last code of its options (a sibling by
    /// construction), every other question keeps the keyword logic.
    /// </summary>
    private sealed class TypeStageScriptClient : IAgentDecisionClient
    {
        public bool Enabled { get; set; } = true;

        public bool AbstainPerType { get; set; }

        /// <summary>Count of the differentiation questions this client saw.</summary>
        public int PerTypeQuestions { get; private set; }

        /// <summary>The scripted sibling answer of the first differentiation question.</summary>
        public string? SiblingAnswer { get; private set; }

        public string ProviderName => "test";

        public string Model => "test-model";

        public bool IsEnabled => Enabled;

        public Task<IReadOnlyDictionary<string, AgentDecisionAnswer>> AskAsync(
            string state,
            IReadOnlyDictionary<string, AgentDecisionQuestion> questions,
            CancellationToken ct)
        {
            var answers = new Dictionary<string, AgentDecisionAnswer>(StringComparer.Ordinal);
            var perType = state.Contains("SIBLING", StringComparison.Ordinal);
            foreach (var pair in questions)
            {
                if (perType)
                {
                    PerTypeQuestions++;
                    var codes = pair.Value.Criteria!.Keys
                        .Where(code => !string.Equals(code, NoneId, StringComparison.Ordinal))
                        .OrderBy(code => code, StringComparer.Ordinal)
                        .ToList();
                    if (AbstainPerType)
                    {
                        answers[pair.Key] = new AgentDecisionAnswer
                        {
                            Type = "choice",
                            Probabilities = new Dictionary<string, double> { [NoneId] = 0.9 }
                        };
                        continue;
                    }

                    if (PerTypeQuestions == 1)
                    {
                        SiblingAnswer = codes[^1];
                        answers[pair.Key] = new AgentDecisionAnswer
                        {
                            Type = "choice",
                            Probabilities = new Dictionary<string, double> { [codes[^1]] = 0.9 }
                        };
                        continue;
                    }
                }

                answers[pair.Key] = KeywordAnswer(pair.Value);
            }

            return Task.FromResult<IReadOnlyDictionary<string, AgentDecisionAnswer>>(answers);
        }

        /// <summary>The KeywordDecisionClient logic, copied: token overlap ranking.</summary>
        private static AgentDecisionAnswer KeywordAnswer(AgentDecisionQuestion question)
        {
            var marker = question.Instructions.IndexOf("Match: ", StringComparison.Ordinal);
            var query = marker >= 0
                ? question.Instructions.Substring(marker + "Match: ".Length)
                : question.Instructions;
            var queryTokens = TokensOf(query);
            var ranked = question.Criteria!
                .Select(option => (
                    Id: option.Key,
                    Overlap: TokensOf(option.Value ?? string.Empty)
                        .Count(token => queryTokens.Any(q => Matches(q, token)))))
                .OrderByDescending(option => option.Overlap)
                .ThenBy(option => option.Id, StringComparer.Ordinal)
                .ToList();

            var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
            var rank = 0;
            foreach (var option in ranked.Where(option => option.Overlap > 0).Take(5))
            {
                probabilities[option.Id] = System.Math.Max(0.05, 0.9 - rank * 0.1);
                rank++;
            }

            if (probabilities.Count == 0)
                probabilities[ranked[0].Id] = 0.9;

            return new AgentDecisionAnswer
            {
                Type = "choice",
                Probabilities = probabilities
            };
        }

        private static bool Matches(string left, string right) =>
            string.Equals(left, right, StringComparison.Ordinal)
            || (System.Math.Min(left.Length, right.Length) >= 5
                && (left.StartsWith(right, StringComparison.Ordinal)
                    || right.StartsWith(left, StringComparison.Ordinal)));
    }
}
