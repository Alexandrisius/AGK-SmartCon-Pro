using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.IntegrationTests.Agent.Support;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Classification;
using SmartCon.Revit.Agent.Query;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// classify_types against the table the document really carries: every test
/// starts from a fresh document, loads the sample tab-format table through the
/// real loader and drives the semantic stages with a deterministic keyword
/// decision client. The end-to-end test feeds the returned setParameters items
/// into the REAL set_parameters tool and checks the code and the description
/// Revit fills from the table row.
/// </summary>
public sealed partial class AgentClassifyTypesTests : RevitApiTest
{
    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private List<long> _wallTypeIds = null!;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();
        // The wall types the tool itself would select: OST_Walls category and
        // the Assembly Code parameter. Curtain wall types belong to another
        // category and are filtered out - the expectations rely on that.
        var wallsCategory = Category.GetCategory(Doc, BuiltInCategory.OST_Walls);
        _wallTypeIds = new FilteredElementCollector(Doc)
            .OfClass(typeof(WallType))
            .WhereElementIsElementType()
            .Cast<WallType>()
            .Where(type => wallsCategory is not null
                           && type.Category is not null
                           && type.Category.Id == wallsCategory.Id
                           && type.get_Parameter(AgentParameterResolver.AssemblyCodeBuiltIn) is not null)
            .Select(type => type.Id.GetValue())
            .ToList();
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static JsonElement Args(object value)
    {
        using var document = JsonSerializer.SerializeToDocument(value);
        return document.RootElement.Clone();
    }

    private void RequireWallTypes(int minimum = 1)
    {
        if (_wallTypeIds.Count < minimum)
            Skip.Test("В шаблоне проекта по умолчанию меньше " + minimum + " типов стены");
    }

    private ToolResult LoadSampleTable(string fileName = "classify-sample.txt")
    {
        var path = ClassificationFixtures.WriteTable(
            fileName, ClassificationFixtures.SampleTable, ClassificationFixtures.Utf16);
        return new LoadClassificationTableTool(new StubRevitContext(Doc), _transactions!, _changeSets!)
            .ExecuteForTests(Args(new { path, dryRun = false }), approved: true, CancellationToken.None);
    }

    private Task<ToolResult> ClassifyAsync(
        object args, KeywordDecisionClient? client = null) =>
        new ClassifyTypesTool(
                new StubRevitContext(Doc),
                new AgentSemanticRanker(client ?? new KeywordDecisionClient()))
            .ExecuteForTestsAsync(Doc, Args(args), CancellationToken.None);

    private ToolResult Set(JsonElement items, bool dryRun) =>
        new SetParametersTool(new StubRevitContext(Doc), _transactions!, _changeSets!)
            .ExecuteForTests(
                Args(new { items, dryRun }),
                approved: true,
                CancellationToken.None);

    private string? CodeOf(long typeId)
    {
        var parameter = Doc.GetElement(ElementIdCompat.Create(typeId))?
            .get_Parameter(AgentParameterResolver.AssemblyCodeBuiltIn);
        if (parameter is null || !parameter.HasValue)
            return null;

        var code = parameter.AsString();
        return string.IsNullOrWhiteSpace(code) ? null : code;
    }

    private string? DescriptionOf(long typeId) =>
        Doc.GetElement(ElementIdCompat.Create(typeId))?
            .get_Parameter(AgentParameterResolver.AssemblyDescriptionBuiltIn)?
            .AsString();

    // ── A document without a classifier ─────────────────────────────────

    [Test]
    public async Task Classify_WithoutTable_ReportsTableNotLoaded()
    {
        RequireWallTypes();

        var result = await ClassifyAsync(new { categories = new[] { "OST_Walls" } });

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("classification_table_not_loaded");
        await Assert.That(result.Data!.Value.GetProperty("loaded").GetBoolean()).IsFalse();
        await Assert.That(result.Message).Contains("load_classification_table");
        await Assert.That(result.Message).Contains("never pick codes from memory or by analogy");
    }

    [Test]
    public async Task Classify_DisabledClient_ReturnsDecisionUnavailable()
    {
        RequireWallTypes();
        await Assert.That(LoadSampleTable("classify-disabled.txt").Success).IsTrue();

        var result = await ClassifyAsync(
            new { categories = new[] { "OST_Walls" } },
            new KeywordDecisionClient { Enabled = false });

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("decision_unavailable");
        await Assert.That(result.Message).Contains("decision model is not available");
    }

    // ── With the sample table loaded ────────────────────────────────────

    [Test]
    public async Task Classify_WithTable_ProposesWallCodeFromTheTable()
    {
        RequireWallTypes();
        await Assert.That(LoadSampleTable("classify-walls.txt").Success).IsTrue();

        var result = await ClassifyAsync(new { categories = new[] { "OST_Walls" } });

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("source").GetProperty("loaded").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("selection").GetProperty("selected").GetInt32())
            .IsEqualTo(_wallTypeIds.Count);

        var assignments = data.GetProperty("assignments");
        await Assert.That(assignments.GetArrayLength()).IsGreaterThan(0);
        var first = assignments[0];
        await Assert.That(first.GetProperty("code").GetString()).IsEqualTo("10.10.10");
        await Assert.That(first.GetProperty("breadcrumb").GetString())
            .IsEqualTo("Shell › Walls › Load-bearing walls");

        var items = data.GetProperty("setParameters").GetProperty("items");
        await Assert.That(items.GetArrayLength()).IsGreaterThan(0);
        await Assert.That(items.EnumerateArray().Any(item =>
            item.GetProperty("elementId").GetInt64() == _wallTypeIds[0]
            && item.GetProperty("parameter").GetString() == "UNIFORMAT_CODE"
            && item.GetProperty("value").GetString() == "10.10.10"
            && item.GetProperty("scope").GetString() == "type")).IsTrue();
        await Assert.That(result.Message).Contains("Proposed a code for");
    }

    [Test]
    public async Task Classify_LowScore_LandsInUncertainWithoutSetItems()
    {
        RequireWallTypes();
        await Assert.That(LoadSampleTable("classify-low.txt").Success).IsTrue();

        var result = await ClassifyAsync(
            new { categories = new[] { "OST_Walls" } },
            new KeywordDecisionClient { Score = 0.3 });

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        var summary = data.GetProperty("summary");
        await Assert.That(summary.GetProperty("lowConfidence").GetInt32()).IsGreaterThan(0);
        await Assert.That(summary.GetProperty("proposed").GetInt32()).IsEqualTo(0);

        var uncertain = data.GetProperty("uncertain");
        await Assert.That(uncertain.GetArrayLength()).IsGreaterThan(0);
        var first = uncertain[0];
        await Assert.That(first.GetProperty("reason").GetString()).IsEqualTo("low_confidence");
        await Assert.That(first.GetProperty("candidates").GetArrayLength()).IsGreaterThan(0);

        await Assert.That(data.GetProperty("setParameters").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task Classify_OnlyEmpty_PresetCodeIsSkipped()
    {
        RequireWallTypes();
        await Assert.That(LoadSampleTable("classify-preset.txt").Success).IsTrue();
        var preset = JsonSerializer.SerializeToElement(new object[]
        {
            new
            {
                elementId = _wallTypeIds[0],
                parameter = "ASSEMBLY_CODE",
                value = "10.10.10",
                scope = "type"
            }
        });
        await Assert.That(Set(preset, dryRun: false).Success).IsTrue();

        var result = await ClassifyAsync(new { categories = new[] { "OST_Walls" } });

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("selection").GetProperty("skippedAlreadySet").GetInt32())
            .IsGreaterThanOrEqualTo(1);
        await Assert.That(data.GetProperty("selection").GetProperty("selected").GetInt32())
            .IsEqualTo(_wallTypeIds.Count - 1);
        await Assert.That(data.GetProperty("setParameters").GetProperty("items").EnumerateArray()
            .All(item => item.GetProperty("elementId").GetInt64() != _wallTypeIds[0])).IsTrue();
    }

    [Test]
    public async Task Classify_OnlyEmptyFalse_PresetCodeIsAlreadySet()
    {
        RequireWallTypes();
        await Assert.That(LoadSampleTable("classify-already.txt").Success).IsTrue();
        var preset = JsonSerializer.SerializeToElement(new object[]
        {
            new
            {
                elementId = _wallTypeIds[0],
                parameter = "ASSEMBLY_CODE",
                value = "10.10.10",
                scope = "type"
            }
        });
        await Assert.That(Set(preset, dryRun: false).Success).IsTrue();

        var result = await ClassifyAsync(
            new { categories = new[] { "OST_Walls" }, onlyEmpty = false });

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("summary").GetProperty("alreadySet").GetInt32())
            .IsGreaterThanOrEqualTo(1);
        await Assert.That(data.GetProperty("selection").GetProperty("selected").GetInt32())
            .IsEqualTo(_wallTypeIds.Count);
        await Assert.That(data.GetProperty("setParameters").GetProperty("items").EnumerateArray()
            .All(item => item.GetProperty("elementId").GetInt64() != _wallTypeIds[0])).IsTrue();
    }

    [Test]
    public async Task Classify_MaxTypes_TruncatesTheSelectionWithHint()
    {
        RequireWallTypes(2);
        await Assert.That(LoadSampleTable("classify-truncate.txt").Success).IsTrue();

        var result = await ClassifyAsync(
            new { categories = new[] { "OST_Walls" }, maxTypes = 1 });

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("selection").GetProperty("selected").GetInt32())
            .IsEqualTo(1);
        await Assert.That(data.GetProperty("selection").GetProperty("truncated").GetBoolean())
            .IsTrue();
        await Assert.That(data.GetProperty("hint").GetString()).Contains("maxTypes/maxFamilies");
    }

    [Test]
    public async Task Classify_NothingMatches_OkWithZeroSelected()
    {
        await Assert.That(LoadSampleTable("classify-empty.txt").Success).IsTrue();

        var result = await ClassifyAsync(new { nameContains = "zzz-no-such-type" });

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("selection").GetProperty("selected").GetInt32())
            .IsEqualTo(0);
        await Assert.That(data.GetProperty("assignments").GetArrayLength()).IsEqualTo(0);
        await Assert.That(data.GetProperty("setParameters").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(data.GetProperty("note").GetString()).Contains("Nothing to classify");
    }

    [Test]
    public async Task Classify_EndToEnd_SetParametersWritesCodeAndRevitFillsDescription()
    {
        RequireWallTypes();
        await Assert.That(LoadSampleTable("classify-e2e.txt").Success).IsTrue();

        var classified = await ClassifyAsync(new { categories = new[] { "OST_Walls" } });
        await Assert.That(classified.Success).IsTrue();
        var items = classified.Data!.Value.GetProperty("setParameters").GetProperty("items");
        await Assert.That(items.GetArrayLength()).IsGreaterThan(0);

        var dryRun = Set(items, dryRun: true);
        await Assert.That(dryRun.Success).IsTrue();
        await Assert.That(CodeOf(_wallTypeIds[0])).IsNull();

        var applied = Set(items, dryRun: false);
        await Assert.That(applied.Success).IsTrue();

        await Assert.That(CodeOf(_wallTypeIds[0])).IsEqualTo("10.10.10");
        // Revit fills the Assembly Description from the table row of the code.
        await Assert.That(DescriptionOf(_wallTypeIds[0])).IsEqualTo("Load-bearing walls");
    }

    // ── Typed argument errors and filters (R10) ─────────────────────────

    [Test]
    public async Task Classify_UnknownTypeId_IsTypeNotFound()
    {
        await Assert.That(LoadSampleTable("classify-bad-type.txt").Success).IsTrue();

        var result = await ClassifyAsync(new { typeIds = new long[] { 999999999 } });

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("type_not_found");
        await Assert.That(result.Message).Contains("999999999");
    }

    [Test]
    public async Task Classify_UnknownFamilyId_IsFamilyNotFound()
    {
        await Assert.That(LoadSampleTable("classify-bad-family.txt").Success).IsTrue();

        var result = await ClassifyAsync(new { familyIds = new long[] { 999999999 } });

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("family_not_found");
    }

    [Test]
    public async Task Classify_UnknownCategory_IsUnknownCategory()
    {
        await Assert.That(LoadSampleTable("classify-bad-cat.txt").Success).IsTrue();

        var result = await ClassifyAsync(new { categories = new[] { "OST_NoSuchCategory" } });

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("unknown_category");
        await Assert.That(result.Message).Contains("OST_NoSuchCategory");
    }

    [Test]
    public async Task Classify_OutOfRangeArguments_AreInvalidArguments()
    {
        await Assert.That(LoadSampleTable("classify-ranges.txt").Success).IsTrue();
        var cases = new (string Name, object Value)[]
        {
            ("maxTypes", 0), ("maxTypes", 2001),
            ("maxFamilies", 0), ("maxFamilies", 601),
            ("minScore", 1.5), ("minScore", -0.1),
            ("candidateDepth", 1), ("candidateDepth", 16)
        };
        foreach (var (name, value) in cases)
        {
            var result = await ClassifyAsync(
                new Dictionary<string, object> { [name] = value });
            await Assert.That(result.Success).IsFalse();
            await Assert.That(result.ErrorCode).IsEqualTo("invalid_arguments");
            await Assert.That(result.Message).Contains(name);
        }
    }

    [Test]
    public async Task Classify_TextParameters_UnknownNameIsWarnedNotSwallowed()
    {
        RequireWallTypes();
        await Assert.That(LoadSampleTable("classify-text-params.txt").Success).IsTrue();

        var result = await ClassifyAsync(new
        {
            categories = new[] { "OST_Walls" },
            textParameters = new[] { "ALL_MODEL_DESCRIPTION", "НетТакогоПараметра" }
        });

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Warnings.Any(warning =>
            warning.Contains("НетТакогоПараметра") && warning.Contains("textParameters"))).IsTrue();
        await Assert.That(result.Warnings.Any(warning => warning.Contains("ALL_MODEL_DESCRIPTION")))
            .IsFalse();
    }

    [Test]
    public async Task Classify_MaxFamilies_TruncatesWithSkippedCount()
    {
        RequireWallTypes();
        await Assert.That(LoadSampleTable("classify-max-families.txt").Success).IsTrue();

        // Wall families only: the very first family of a bare template can be
        // a text-less system family (honestly no_candidates), so the budget is
        // probed inside a niche that always carries descriptive text.
        var full = await ClassifyAsync(new { categories = new[] { "OST_Walls" } });
        await Assert.That(full.Success).IsTrue();
        var families = full.Data!.Value.GetProperty("summary").GetProperty("familiesProcessed").GetInt32();
        if (families < 2)
            Skip.Test("The template has fewer than two wall families with a classification code");

        var result = await ClassifyAsync(new
        {
            categories = new[] { "OST_Walls" },
            maxFamilies = 1
        });

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        var summary = data.GetProperty("summary");
        await Assert.That(summary.GetProperty("familiesProcessed").GetInt32()).IsEqualTo(1);
        await Assert.That(summary.GetProperty("familiesSkipped").GetInt32()).IsEqualTo(families - 1);
        await Assert.That(data.GetProperty("selection").GetProperty("truncated").GetBoolean())
            .IsTrue();
        await Assert.That(data.GetProperty("hint").GetString()).Contains("maxFamilies");
    }

    [Test]
    public async Task Classify_SetParametersArgument_MatchesTheRealSetParametersSchema()
    {
        RequireWallTypes();
        await Assert.That(LoadSampleTable("classify-schema.txt").Success).IsTrue();

        var classified = await ClassifyAsync(new { categories = new[] { "OST_Walls" } });
        await Assert.That(classified.Success).IsTrue();
        var setParameters = classified.Data!.Value.GetProperty("setParameters");
        await Assert.That(setParameters.ValueKind).IsEqualTo(JsonValueKind.Object);

        // The schema of the REAL set_parameters tool: the argument must pass it
        // as is (R7) - only schema keys, items of the schema shape.
        var schemaJson = new SetParametersTool(
                new StubRevitContext(Doc), _transactions!, _changeSets!)
            .Definition.InputSchemaJson;
        using var schemaDocument = JsonDocument.Parse(schemaJson);
        var schema = schemaDocument.RootElement;
        var allowed = schema.GetProperty("properties").EnumerateObject()
            .Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var property in setParameters.EnumerateObject())
        {
            await Assert.That(allowed.Contains(property.Name)).IsTrue();
        }

        // setParameters is either {items} (one call) or {calls:[{items}…]} (the
        // tool takes at most 500 items per call): every items list must pass the
        // schema as is (R7) - only schema keys, items of the schema shape.
        var itemLists = setParameters.TryGetProperty("calls", out var calls)
            ? calls.EnumerateArray()
                .Select(call => call.GetProperty("items"))
                .ToList()
            : new List<JsonElement> { setParameters.GetProperty("items") };

        var itemsSchema = schema.GetProperty("properties").GetProperty("items");
        var itemAllowed = itemsSchema.GetProperty("items").GetProperty("properties")
            .EnumerateObject().Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        var itemRequired = itemsSchema.GetProperty("items").GetProperty("required")
            .EnumerateArray().Select(value => value.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var items in itemLists)
        {
            await Assert.That(items.GetArrayLength()).IsGreaterThan(0);
            await Assert.That(items.GetArrayLength() <= 500).IsTrue();
            foreach (var item in items.EnumerateArray())
            {
                foreach (var property in item.EnumerateObject())
                {
                    await Assert.That(itemAllowed.Contains(property.Name)).IsTrue();
                }

                foreach (var required in itemRequired)
                {
                    await Assert.That(item.TryGetProperty(required, out _)).IsTrue();
                }
            }
        }
    }

    /// <summary>
    /// Deterministic stand-in for the decision model: every option is scored by
    /// the tokens it shares with the question's Match line; without any overlap
    /// the first option by code order wins (the plumbing, not the model
    /// quality, is what these tests verify).
    /// </summary>
    private sealed class KeywordDecisionClient : IAgentDecisionClient
    {
        public double Score { get; set; } = 0.9;

        public bool Enabled { get; set; } = true;

        public string ProviderName => "test";

        public string Model => "test-model";

        public bool IsEnabled => Enabled;

        public Task<IReadOnlyDictionary<string, AgentDecisionAnswer>> AskAsync(
            string state,
            IReadOnlyDictionary<string, AgentDecisionQuestion> questions,
            CancellationToken ct)
        {
            var answers = new Dictionary<string, AgentDecisionAnswer>(StringComparer.Ordinal);
            foreach (var pair in questions)
            {
                var marker = pair.Value.Instructions.IndexOf("Match: ", StringComparison.Ordinal);
                var query = marker >= 0
                    ? pair.Value.Instructions.Substring(marker + "Match: ".Length)
                    : pair.Value.Instructions;
                var queryTokens = Tokens(query);
                var ranked = pair.Value.Criteria!
                    .Select(option => (
                        Id: option.Key,
                        Overlap: Tokens(option.Value ?? string.Empty)
                            .Count(token => queryTokens.Any(q => Matches(q, token)))))
                    .OrderByDescending(option => option.Overlap)
                    .ThenBy(option => option.Id, StringComparer.Ordinal)
                    .ToList();

                var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
                var rank = 0;
                foreach (var option in ranked.Where(option => option.Overlap > 0).Take(5))
                {
                    probabilities[option.Id] = System.Math.Max(0.05, Score - rank * 0.1);
                    rank++;
                }

                if (probabilities.Count == 0)
                    probabilities[ranked[0].Id] = Score;

                answers[pair.Key] = new AgentDecisionAnswer
                {
                    Type = "choice",
                    Probabilities = probabilities
                };
            }

            return Task.FromResult<IReadOnlyDictionary<string, AgentDecisionAnswer>>(answers);
        }

        private static HashSet<string> Tokens(string text)
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            var current = new System.Text.StringBuilder();
            foreach (var character in text)
            {
                if (char.IsLetterOrDigit(character))
                {
                    current.Append(character);
                    continue;
                }

                Flush(current, tokens);
            }

            Flush(current, tokens);
            return tokens;
        }

        private static void Flush(System.Text.StringBuilder builder, HashSet<string> tokens)
        {
            if (builder.Length > 0)
                tokens.Add(builder.ToString().ToLowerInvariant());
            builder.Clear();
        }

        private static bool Matches(string left, string right) =>
            string.Equals(left, right, StringComparison.Ordinal)
            || (System.Math.Min(left.Length, right.Length) >= 5
                && (left.StartsWith(right, StringComparison.Ordinal)
                    || right.StartsWith(left, StringComparison.Ordinal)));
    }
}
