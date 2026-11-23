using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.IntegrationTests.Agent.Support;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Classification;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// The per-type differentiation stage of classify_types on real template types
/// (C5-04): a second type is seeded into one wall family, the table is one the
/// wall family has no lexical hit against (so the family walks down the table
/// and ends with ONE code), and the types are then asked apart. The sibling
/// answer of one type splits the family in assignments and set_parameters, and
/// the REAL set_parameters writes two different codes into two types of one
/// family. Partial of the test class: reuses its seed and helpers.
/// </summary>
public sealed partial class AgentClassifyTypesTests
{
    private const string NoneId = "__none__";
    private const string StageFamilyCode = "10.10.10";
    private const string StageSiblingCode = "10.10.20";

    /// <summary>
    /// A table the wall families share no token with: the family text carries
    /// only the category and the family name, so every wall family is weak,
    /// walks the table by code order (10 → 10.10 → 10.10.10) and ends on ONE
    /// code whose parent holds a sibling leaf.
    /// </summary>
    private const string TypeStageTable =
        "10\tShell\t1\t\n" +
        "10.10\tVertical barriers\t2\t\n" +
        "10.10.10\tLoad-bearing\t3\t\n" +
        "10.10.20\tPartitions\t3\t\n" +
        "20\tServices\t1\t\n" +
        "20.10\tPipelines\t2\t\n" +
        "20.10.10\tPipes\t3\t\n";

    /// <summary>Classify with an arbitrary decision client (the shared helper takes its own).</summary>
    private Task<ToolResult> ClassifyWithAsync(object args, IAgentDecisionClient client) =>
        new ClassifyTypesTool(
                new StubRevitContext(Doc),
                new AgentSemanticRanker(client))
            .ExecuteForTestsAsync(Doc, Args(args), CancellationToken.None);

    private ToolResult LoadTypeStageTable(string fileName) =>
        new LoadClassificationTableTool(new StubRevitContext(Doc), _transactions!, _changeSets!)
            .ExecuteForTests(
                Args(new
                {
                    path = ClassificationFixtures.WriteTable(fileName, TypeStageTable, ClassificationFixtures.Utf16),
                    dryRun = false
                }),
                approved: true,
                CancellationToken.None);

    [Test]
    public async Task Classify_OneCodeFamily_SiblingAnswerSplitsTheFamily_AndSetParametersWritesBothCodes()
    {
        var (originalId, copyId, family) = SeedSecondWallType();
        await Assert.That(LoadTypeStageTable("classify-type-stage.txt").Success).IsTrue();

        var client = new TypeStageClient();
        var result = await ClassifyWithAsync(new { categories = new[] { "OST_Walls" } }, client);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;

        // The stage really ran: the family went down the table to one code and
        // its two types were asked apart - one question per type text.
        await Assert.That(client.PerTypeQueries.Count(query => query.Contains(family, StringComparison.Ordinal)))
            .IsGreaterThanOrEqualTo(2);
        await Assert.That(client.DescentCalls).IsGreaterThanOrEqualTo(2);
        await Assert.That(data.GetProperty("summary").GetProperty("decisionCalls").GetInt32())
            .IsEqualTo(client.Calls);

        // One family, two codes: two assignment rows of the seeded family (the
        // report orders them by code), one type each.
        var rows = FamilyRows(data.GetProperty("assignments"), family);
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(string.Join("|", rows.Select(row => row.GetProperty("code").GetString())))
            .IsEqualTo(StageFamilyCode + "|" + StageSiblingCode);
        await Assert.That(string.Join("|", rows.Select(IdsOf)))
            .IsEqualTo(IdText(originalId) + "|" + IdText(copyId));

        // The READY argument carries both values for the two types of one family.
        var items = data.GetProperty("setParameters").GetProperty("items").EnumerateArray().ToList();
        await Assert.That(ValueOf(items, originalId)).IsEqualTo(StageFamilyCode);
        await Assert.That(ValueOf(items, copyId)).IsEqualTo(StageSiblingCode);
        await Assert.That(result.Message).Contains("Proposed a code for");

        // The real tool writes them: two codes - and two descriptions filled by
        // Revit from the two table rows - inside ONE family.
        var dryRun = Set(data.GetProperty("setParameters").GetProperty("items"), dryRun: true);
        await Assert.That(dryRun.Success).IsTrue();
        await Assert.That(CodeOf(originalId)).IsNull();
        await Assert.That(CodeOf(copyId)).IsNull();
        await AssertReadBackMatchesChanged(dryRun);

        var applied = Set(data.GetProperty("setParameters").GetProperty("items"), dryRun: false);
        await Assert.That(applied.Success).IsTrue();
        await AssertReadBackMatchesChanged(applied);
        await Assert.That(CodeOf(originalId)).IsEqualTo(StageFamilyCode);
        await Assert.That(CodeOf(copyId)).IsEqualTo(StageSiblingCode);
        await Assert.That(DescriptionOf(originalId)).IsEqualTo("Load-bearing");
        await Assert.That(DescriptionOf(copyId)).IsEqualTo("Partitions");
    }

    [Test]
    public async Task Classify_OneCodeFamily_PerTypeAbstain_LandsInUncertainWithTheFamilyCode()
    {
        var (originalId, copyId, family) = SeedSecondWallType();
        await Assert.That(LoadTypeStageTable("classify-type-stage-abstain.txt").Success).IsTrue();

        var client = new TypeStageClient { AbstainPerType = true };
        var result = await ClassifyWithAsync(new { categories = new[] { "OST_Walls" } }, client);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(client.PerTypeQueries.Count(query => query.Contains(family, StringComparison.Ordinal)))
            .IsGreaterThanOrEqualTo(2);

        // An honest none is never written as a confident code: nothing of the
        // family is proposed, both types are uncertain with the family code as
        // the candidate to confirm.
        await Assert.That(FamilyRows(data.GetProperty("assignments"), family).Count).IsEqualTo(0);
        var items = data.GetProperty("setParameters").GetProperty("items").EnumerateArray().ToList();
        await Assert.That(items.Any(item => IdOf(item) == originalId || IdOf(item) == copyId)).IsFalse();

        var uncertain = FamilyRows(data.GetProperty("uncertain"), family);
        var typeIds = uncertain
            .SelectMany(group => group.GetProperty("typeIds").EnumerateArray())
            .Select(id => id.GetInt64())
            .OrderBy(id => id)
            .Select(IdText)
            .ToList();
        var expected = new[] { copyId, originalId }.OrderBy(id => id).Select(IdText).ToList();
        await Assert.That(string.Join("|", typeIds)).IsEqualTo(string.Join("|", expected));
        await Assert.That(uncertain.All(group =>
            group.GetProperty("reason").GetString()!.Contains("neither the family code nor its siblings", StringComparison.Ordinal)
            && group.GetProperty("candidates")[0].GetProperty("code").GetString() == StageFamilyCode)).IsTrue();
        await Assert.That(data.GetProperty("summary").GetProperty("lowConfidence").GetInt32())
            .IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task Classify_OneCodeFamily_PerTypeCallThrows_KeepsTheFamilyCodesAndWarns()
    {
        var (originalId, copyId, family) = SeedSecondWallType();
        await Assert.That(LoadTypeStageTable("classify-type-stage-failure.txt").Success).IsTrue();

        var client = new TypeStageClient { ThrowPerType = true };
        var result = await ClassifyWithAsync(new { categories = new[] { "OST_Walls" } }, client);

        // A failure of the stage is not a failure of the call: the codes the
        // family already has stand, and the warning says what was left.
        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.TryGetProperty("incomplete", out _)).IsFalse();
        var row = FamilyRows(data.GetProperty("assignments"), family).Single();
        await Assert.That(row.GetProperty("code").GetString()).IsEqualTo(StageFamilyCode);
        await Assert.That(string.Join("|", row.GetProperty("typeIds").EnumerateArray().Select(id => IdText(id.GetInt64())).OrderBy(id => id, StringComparer.Ordinal)))
            .IsEqualTo(string.Join("|", new[] { copyId, originalId }.OrderBy(id => id).Select(IdText)));

        await Assert.That(result.Warnings.Any(warning =>
            warning.Contains("decision layer failed (decision_call_failed: HttpClient timeout)", StringComparison.Ordinal)
            && warning.Contains("[Action: accept the family codes", StringComparison.Ordinal))).IsTrue();
        await Assert.That(data.GetProperty("summary").GetProperty("decisionCalls").GetInt32())
            .IsEqualTo(client.Calls);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// The seed of the stage tests: a SECOND type in the family of the first
    /// wall type of the template. The family text stays what it was (a type
    /// name never enters it), while the new type's name adds the token of the
    /// sibling row - so the two types differ by more than size and the keyword
    /// client answers them differently. Skips the test when the template offers
    /// no such family or its names already touch the rows of the test table
    /// (the template depends on the machine).
    /// </summary>
    private (long OriginalId, long CopyId, string Family) SeedSecondWallType()
    {
        RequireWallTypes(1);
        var original = (WallType)Doc.GetElement(ElementIdCompat.Create(_wallTypeIds[0]));
        var family = original.FamilyName ?? string.Empty;
        var text = (original.Category?.Name ?? string.Empty) + " " + family + " " + original.Name;
        var tableTokens = TokensOf(string.Join(" ", new[] { "Shell", "Vertical barriers", "Load-bearing", "Partitions", "Services", "Pipelines", "Pipes" }));
        if (TokensOf(text).Any(token => tableTokens.Any(tableToken => TokensMatch(token, tableToken))))
        {
            Skip.Test("Имена категории, семейства или типа стены шаблона пересекаются со строками тестовой таблицы (" +
                      text + ") - тест этапа различения типов к этому шаблону неприменим");
            return (0, 0, string.Empty);
        }

        long copyId = 0;
        var originalId = _wallTypeIds[0];
        var committed = _transactions!.RunInTransaction(Doc, "Seed a second wall type", doc =>
        {
            var type = (WallType)doc.GetElement(ElementIdCompat.Create(originalId))!;
            copyId = type.Duplicate(type.Name + " partition").Id.GetValue();
        });
        if (!committed || copyId == 0 || Doc.GetElement(ElementIdCompat.Create(copyId)) is not WallType)
        {
            Skip.Test("Шаблон не позволил создать второй тип в семействе стен (" + family + ")");
            return (0, 0, string.Empty);
        }

        return (originalId, copyId, family);
    }

    /// <summary>The rows of one family in assignments or uncertain groups.</summary>
    private static List<JsonElement> FamilyRows(JsonElement rows, string family) =>
        rows.EnumerateArray()
            .Where(row => string.Equals(row.GetProperty("family").GetString(), family, StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static long IdOf(JsonElement item) => item.GetProperty("elementId").GetInt64();

    private static string IdText(long id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The typeIds of one report row, comma-joined in the row's order.</summary>
    private static string IdsOf(JsonElement row) =>
        string.Join(",", row.GetProperty("typeIds").EnumerateArray().Select(id => IdText(id.GetInt64())));

    private static string? ValueOf(List<JsonElement> items, long typeId) =>
        items.FirstOrDefault(item => IdOf(item) == typeId).ValueKind == JsonValueKind.Undefined
            ? null
            : items.First(item => IdOf(item) == typeId).GetProperty("value").GetString();

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

            TypeStageFlush(builder, tokens);
        }

        TypeStageFlush(builder, tokens);
        return tokens;
    }

    private static void TypeStageFlush(StringBuilder builder, HashSet<string> tokens)
    {
        if (builder.Length > 0)
        {
            var token = builder.ToString().ToLowerInvariant();
            if (!token.All(char.IsDigit))
                tokens.Add(token);
        }

        builder.Clear();
    }

    private static bool TokensMatch(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
        || (System.Math.Min(left.Length, right.Length) >= 5
            && (left.StartsWith(right, StringComparison.OrdinalIgnoreCase)
                || right.StartsWith(left, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The keyword decision client of this partial: every option is scored by
    /// the tokens it shares with the question's Match line (equal or a prefix
    /// of at least five characters); without an overlap the first option by
    /// code order wins. The honest none never wins by shared words - a real
    /// model does not choose "none" because of them. The differentiation
    /// questions are recognized by their SIBLING state and can be answered
    /// with the none or failed as a whole.
    /// </summary>
    private sealed class TypeStageClient : IAgentDecisionClient
    {
        public bool Enabled { get; set; } = true;

        /// <summary>Answer every differentiation question with the honest none.</summary>
        public bool AbstainPerType { get; set; }

        /// <summary>Fail the differentiation call with a transport error.</summary>
        public bool ThrowPerType { get; set; }

        /// <summary>The Match lines of the differentiation questions the client saw.</summary>
        public List<string> PerTypeQueries { get; } = new();

        /// <summary>Decision calls of any stage.</summary>
        public int Calls { get; private set; }

        /// <summary>Calls that walked a family down the table.</summary>
        public int DescentCalls { get; private set; }

        public string ProviderName => "test";

        public string Model => "test-model";

        public bool IsEnabled => Enabled;

        public Task<IReadOnlyDictionary<string, AgentDecisionAnswer>> AskAsync(
            string state,
            IReadOnlyDictionary<string, AgentDecisionQuestion> questions,
            CancellationToken ct)
        {
            Calls++;
            var perType = state.Contains("SIBLING", StringComparison.Ordinal);
            if (state.Contains("descend the classification table", StringComparison.Ordinal))
                DescentCalls++;
            var answers = new Dictionary<string, AgentDecisionAnswer>(StringComparer.Ordinal);
            if (perType)
            {
                PerTypeQueries.AddRange(questions.Values.Select(question => question.Instructions));
                if (ThrowPerType)
                    throw new InvalidOperationException("HttpClient timeout");
            }

            foreach (var pair in questions)
            {
                if (perType && AbstainPerType)
                {
                    answers[pair.Key] = new AgentDecisionAnswer
                    {
                        Type = "choice",
                        Probabilities = new Dictionary<string, double> { [NoneId] = 0.9 }
                    };
                    continue;
                }

                answers[pair.Key] = Keyword(pair.Value);
            }

            return Task.FromResult<IReadOnlyDictionary<string, AgentDecisionAnswer>>(answers);
        }

        private static AgentDecisionAnswer Keyword(AgentDecisionQuestion question)
        {
            var marker = question.Instructions.IndexOf("Match: ", StringComparison.Ordinal);
            var query = marker >= 0
                ? question.Instructions.Substring(marker + "Match: ".Length)
                : question.Instructions;
            var queryTokens = TokensOf(query);
            var ranked = question.Criteria!
                .Where(option => !string.Equals(option.Key, NoneId, StringComparison.Ordinal))
                .Select(option => (
                    Id: option.Key,
                    Overlap: TokensOf(option.Value ?? string.Empty)
                        .Count(token => queryTokens.Any(queryToken => TokensMatch(queryToken, token)))))
                .OrderByDescending(option => option.Overlap)
                .ThenBy(option => option.Id, StringComparer.Ordinal)
                .ToList();

            var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
            var rank = 0;
            foreach (var option in ranked.Where(option => option.Overlap > 0))
                probabilities[option.Id] = System.Math.Max(0.05, 0.9 - rank++ * 0.1);

            if (probabilities.Count == 0)
                probabilities[ranked[0].Id] = 0.9;

            return new AgentDecisionAnswer { Type = "choice", Probabilities = probabilities };
        }
    }
}
