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

    private void RequireWallTypes(int minimum = 1)
    {
        if (_wallTypeIds.Count < minimum)
            Skip.Test("В шаблоне проекта по умолчанию меньше " + minimum + " типов стены");
    }

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
        await Assert.That(first.GetProperty("typeIds").EnumerateArray()
            .Any(id => id.GetInt64() == _wallTypeIds[0])).IsTrue();

        var items = data.GetProperty("setParameters").GetProperty("items");
        await Assert.That(items.GetArrayLength()).IsGreaterThan(0);
        await Assert.That(items.EnumerateArray().Any(item =>
            item.GetProperty("elementId").GetInt64() == _wallTypeIds[0]
            && item.GetProperty("parameter").GetString() == "UNIFORMAT_CODE"
            && item.GetProperty("value").GetString() == "10.10.10"
            && item.GetProperty("scope").GetString() == "type")).IsTrue();
        // typeIds of all assignments together are exactly the ids set_parameters writes.
        var assignedTypeIds = assignments.EnumerateArray()
            .SelectMany(assignment => assignment.GetProperty("typeIds").EnumerateArray())
            .Select(id => id.GetInt64())
            .ToHashSet();
        var itemIds = items.EnumerateArray()
            .Select(item => item.GetProperty("elementId").GetInt64())
            .ToHashSet();
        await Assert.That(assignedTypeIds.SetEquals(itemIds)).IsTrue();

        // Wall types all store the code: nobody is left out, everything selected
        // is processed (the default budgets never truncate a small template).
        var selection = data.GetProperty("selection");
        await Assert.That(selection.GetProperty("skippedNotWritable").GetInt32()).IsEqualTo(0);
        await Assert.That(selection.GetProperty("familiesTotal").GetInt32())
            .IsGreaterThanOrEqualTo(1);
        await Assert.That(selection.GetProperty("processed").GetInt32())
            .IsEqualTo(selection.GetProperty("selected").GetInt32());
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
        await Assert.That(first.GetProperty("typeIds").GetArrayLength()).IsGreaterThan(0);

        await Assert.That(summary.GetProperty("uncertainFamilies").GetInt32())
            .IsEqualTo(uncertain.GetArrayLength());
        await Assert.That(summary.GetProperty("uncertainTypes").GetInt32())
            .IsEqualTo(summary.GetProperty("lowConfidence").GetInt32()
                       + summary.GetProperty("noConfidence").GetInt32()
                       + summary.GetProperty("noCandidates").GetInt32());
        await Assert.That(result.Message).Contains("uncertain type(s) in");
        await Assert.That(result.Message).Contains("uncertain is grouped by family");

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

        // The budget is applied to WHOLE families in the tool's order (families
        // sorted by name): simulate the same first-fit to know what must come
        // out for maxTypes=1 on whatever the template offers.
        var families = WallFamiliesInToolOrder();
        var expectedSkipped = new List<string>();
        var expectedProcessed = 0;
        var budget = 1;
        foreach (var (family, count) in families)
        {
            if (count <= budget)
            {
                expectedProcessed += count;
                budget -= count;
            }
            else
            {
                expectedSkipped.Add(family);
            }
        }

        if (expectedSkipped.Count == 0)
            Skip.Test("Бюджет maxTypes=1 не оставил ни одного семейства за подбором");

        var result = await ClassifyAsync(
            new { categories = new[] { "OST_Walls" }, maxTypes = 1 });

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        var selection = data.GetProperty("selection");
        // selected is the pre-budget selection; processed follows the simulation.
        await Assert.That(selection.GetProperty("selected").GetInt32())
            .IsEqualTo(_wallTypeIds.Count);
        await Assert.That(selection.GetProperty("processed").GetInt32())
            .IsEqualTo(expectedProcessed);
        await Assert.That(selection.GetProperty("truncated").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("summary").GetProperty("familiesSkipped").GetInt32())
            .IsEqualTo(expectedSkipped.Count);

        var notProcessed = data.GetProperty("notProcessed");
        await Assert.That(notProcessed.GetProperty("families").GetInt32())
            .IsEqualTo(expectedSkipped.Count);
        var names = notProcessed.GetProperty("familyNames").EnumerateArray()
            .Select(name => name.GetString())
            .ToList();
        foreach (var family in expectedSkipped)
            await Assert.That(names.Contains(family)).IsTrue();
        await Assert.That(data.GetProperty("hint").GetString()).Contains("maxTypes/maxFamilies");
        await Assert.That(result.Message).Contains("TRUNCATED");
    }

    [Test]
    public async Task Classify_MaxTypes_EqualsFirstFamily_ProcessesItWholeAndSkipsTheRest()
    {
        RequireWallTypes(2);
        await Assert.That(LoadSampleTable("classify-truncate-whole.txt").Success).IsTrue();

        var families = WallFamiliesInToolOrder();
        if (families.Count < 2)
            Skip.Test("В шаблоне проекта по умолчанию меньше двух семейств стен с кодом классификации");
        var first = families[0];

        // A budget equal to the first family's type count takes exactly that
        // family whole (a family is never cut in the middle) and leaves the
        // rest named in notProcessed.
        var result = await ClassifyAsync(
            new { categories = new[] { "OST_Walls" }, maxTypes = first.Count });

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        var selection = data.GetProperty("selection");
        await Assert.That(selection.GetProperty("processed").GetInt32())
            .IsEqualTo(first.Count);
        var summary = data.GetProperty("summary");
        await Assert.That(summary.GetProperty("familiesSelected").GetInt32()).IsEqualTo(1);
        await Assert.That(summary.GetProperty("familiesSkipped").GetInt32())
            .IsEqualTo(families.Count - 1);
        await Assert.That(selection.GetProperty("truncated").GetBoolean()).IsTrue();
        var notProcessed = data.GetProperty("notProcessed");
        await Assert.That(notProcessed.GetProperty("families").GetInt32())
            .IsEqualTo(families.Count - 1);
        var names = notProcessed.GetProperty("familyNames").EnumerateArray()
            .Select(name => name.GetString())
            .ToList();
        foreach (var (family, _) in families.Skip(1))
            await Assert.That(names.Contains(family)).IsTrue();
        await Assert.That(names.Contains(first.Family)).IsFalse();
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
        await AssertReadBackMatchesChanged(dryRun);

        var applied = Set(items, dryRun: false);
        await Assert.That(applied.Success).IsTrue();
        await AssertReadBackMatchesChanged(applied);

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
        await Assert.That(summary.GetProperty("familiesSelected").GetInt32()).IsEqualTo(1);
        await Assert.That(summary.GetProperty("familiesSkipped").GetInt32()).IsEqualTo(families - 1);
        await Assert.That(data.GetProperty("selection").GetProperty("truncated").GetBoolean())
            .IsTrue();
        var notProcessed = data.GetProperty("notProcessed");
        await Assert.That(notProcessed.GetProperty("families").GetInt32()).IsEqualTo(families - 1);
        await Assert.That(notProcessed.GetProperty("familyNames").GetArrayLength())
            .IsEqualTo(families - 1);
        await Assert.That(data.GetProperty("hint").GetString()).Contains("maxFamilies");
        await Assert.That(result.Message).Contains("TRUNCATED");
    }

    [Test]
    public async Task Classify_TypesThatCannotStoreTheCode_AreLeftOut()
    {
        await Assert.That(LoadSampleTable("classify-not-writable.txt").Success).IsTrue();
        var type = FindTypeThatCannotStoreTheCode(Doc, requireWritablePath: false);
        if (type is null)
            Skip.Test("В шаблоне проекта по умолчанию нет типов, не способных хранить код классификации");
        var typeId = type!.Id.GetValue();

        // (a) An explicit typeIds with such a type is a typed refusal naming the id.
        var direct = await ClassifyAsync(new { typeIds = new[] { typeId } });
        await Assert.That(direct.Success).IsFalse();
        await Assert.That(direct.ErrorCode).IsEqualTo("parameter_not_supported");
        await Assert.That(direct.Message).Contains("cannot store the Assembly Code");
        await Assert.That(direct.Message).Contains(typeId.ToString());
        await Assert.That(direct.Data!.Value.GetProperty("unsupported").EnumerateArray()
            .Any(id => id.GetInt64() == typeId)).IsTrue();

        // (b) A filter that matches only such types leaves them out and says so.
        var filter = FilterSelectingOnlyUnwritableTypes(Doc, type!);
        if (filter is null)
            Skip.Test("Не удалось подобрать фильтр, выбирающий только типы без записи кода классификации");
        var result = await ClassifyAsync(filter!);
        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        var selection = data.GetProperty("selection");
        await Assert.That(selection.GetProperty("skippedNotWritable").GetInt32())
            .IsGreaterThanOrEqualTo(1);
        await Assert.That(selection.GetProperty("selected").GetInt32()).IsEqualTo(0);
        await Assert.That(data.GetProperty("assignments").GetArrayLength()).IsEqualTo(0);
        await Assert.That(result.Message).Contains("cannot store the Assembly Code");
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
}
