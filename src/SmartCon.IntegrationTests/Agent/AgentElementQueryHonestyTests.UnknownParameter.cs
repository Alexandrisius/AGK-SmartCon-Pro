using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// A parameter that exists on no scanned element is a typed unknown_parameter
/// failure instead of an honest-looking empty result. Partial of
/// <see cref="AgentElementQueryHonestyTests"/>.
/// </summary>
public sealed partial class AgentElementQueryHonestyTests
{
    [Test]
    public async Task QueryElements_ParameterMissingOnEveryElement_ReturnsUnknownParameter()
    {
        RequireWall();
        var realName = WallElement
            .get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Definition?.Name;
        await Assert.That(string.IsNullOrEmpty(realName)).IsFalse();
        var typo = realName + "q";

        var result = Query(WallsQuery(Condition(typo, "eq", "x")));

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("unknown_parameter");
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("index").GetInt32()).IsEqualTo(0);
        await Assert.That(data.GetProperty("parameter").GetString()).IsEqualTo(typo);
        await Assert.That(data.GetProperty("scanned").GetInt32()).IsGreaterThan(0);
        await Assert.That(Strings(data.GetProperty("suggestions")).Contains(realName!)).IsTrue();
        await Assert.That((result.Message ?? string.Empty).Contains("Condition #0")).IsTrue();
        await Assert.That((result.Message ?? string.Empty).Contains("parameter:\"name\"")).IsTrue();
    }

    [Test]
    public async Task QueryElements_ExistsOperatorsOnAbsentParameter_AreNotAnError()
    {
        RequireWall();
        var absent = "SC_No Such Parameter";

        // exists / notExists answer the question themselves: a parameter that is
        // absent everywhere is never reported as unknown.
        var absentExists = Query(WallsQuery(Condition(absent, "exists")));
        await Assert.That(Outcome(absentExists)).IsEqualTo("ok total=0");
        var absentNotExists = Query(WallsQuery(Condition(absent, "notExists")));
        await Assert.That(Outcome(absentNotExists)).IsEqualTo("ok total=0");

        // The engine contract of notExists is "the parameter exists but carries no
        // value" — it is not an error and it is not empty-handed for such a parameter.
        var emptyName = WallElement
            .get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Definition?.Name;
        await Assert.That(string.IsNullOrEmpty(emptyName)).IsFalse();
        var empty = Query(WallsQuery(Condition(emptyName!, "notExists")));
        await Assert.That(empty.Success).IsTrue();
        await Assert.That(ElementIds(empty).Contains(_wallId)).IsTrue();
    }

    [Test]
    public async Task QueryElements_UnknownParameter_ReportedWhateverTheLogic()
    {
        RequireWall();

        // and: the first condition already keeps every element out.
        var and = Query(WallsQuery(
            Condition("name", "contains", "zz-no-such-name") + "," + Condition("TypoZzz", "eq", "1")));
        await Assert.That(and.Success).IsFalse();
        await Assert.That(and.ErrorCode).IsEqualTo("unknown_parameter");
        await Assert.That(and.Data!.Value.GetProperty("index").GetInt32()).IsEqualTo(1);

        // or: the first condition already matched.
        var or = Query(WallsQuery(
            ConditionRaw("name", "exists", "null") + "," + Condition("TypoZzz", "eq", "1"),
            ",\"logic\":\"or\""));
        await Assert.That(or.Success).IsFalse();
        await Assert.That(or.ErrorCode).IsEqualTo("unknown_parameter");
        await Assert.That(or.Data!.Value.GetProperty("index").GetInt32()).IsEqualTo(1);

        // two broken conditions: the first is the failure, the message names the other.
        var twice = Query(WallsQuery(
            Condition("TypoAaa", "eq", "1") + "," + Condition("TypoBbb", "eq", "2")));
        await Assert.That(twice.Success).IsFalse();
        await Assert.That(twice.ErrorCode).IsEqualTo("unknown_parameter");
        await Assert.That(twice.Data!.Value.GetProperty("parameter").GetString()).IsEqualTo("TypoAaa");
        await Assert.That((twice.Message ?? string.Empty).Contains("TypoBbb")).IsTrue();
    }

    [Test]
    public async Task QueryElements_UnknownParameter_NothingScanned_ReturnsEmptyResult()
    {
        RequireWall();
        long emptyLevelId = 0;
        RunInTransaction("Create level without walls", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 30.0);
            level.Name = LevelName + "_Empty";
            emptyLevelId = level.Id.GetValue();
        });

        // Nothing was scanned, so nothing can be claimed about the parameter.
        var result = Query(WallsQuery(
            Condition("TypoZzz", "eq", "1"),
            ",\"levelIds\":[" + emptyLevelId + "]"));
        await Assert.That(result.Success).IsTrue();
        await Assert.That(Total(result)).IsEqualTo(0);
    }

    [Test]
    public async Task NumericOperatorOrUnitOnStructuralField_ReturnsInvalidQuery()
    {
        RequireWall();
        foreach (var op in new[] { "gt", "gte", "lt", "lte" })
        {
            var result = Query(WallsQuery(ConditionRaw("name", op, "10")));
            await Assert.That(result.Success).IsFalse();
            await Assert.That(result.ErrorCode).IsEqualTo("invalid_query");
            await Assert.That((result.Message ?? string.Empty).Contains("structural field")).IsTrue();
        }

        var withUnit = Query(WallsQuery(ConditionRaw("level", "contains", Json("x"), ",\"unit\":\"mm\"")));
        await Assert.That(withUnit.Success).IsFalse();
        await Assert.That(withUnit.ErrorCode).IsEqualTo("invalid_query");

        var summarized = Summarize(
            "{\"categories\":[\"OST_Walls\"],\"groupBy\":\"category\",\"conditions\":[" +
            ConditionRaw("level", "gt", "3") + "]}");
        await Assert.That(summarized.Success).IsFalse();
        await Assert.That(summarized.ErrorCode).IsEqualTo("invalid_query");
    }

    [Test]
    public async Task ToolDefinitions_StateStructuralFieldsAndUnknownParameter()
    {
        var context = new StubRevitContext(Doc);
        var definitions = new[]
        {
            new QueryElementsTool(context).Definition,
            new SummarizeElementsTool(context).Definition
        };

        foreach (var definition in definitions)
        {
            foreach (var token in new[] { "structural field", "unknown_parameter", "name", "family", "type", "category", "level" })
            {
                await Assert.That((definition.Description ?? string.Empty).Contains(token)).IsTrue();
            }
        }

        var schema = new SummarizeElementsTool(context).Definition.InputSchemaJson;
        await Assert.That(schema.Contains("\"family\"")).IsTrue();
    }
}
