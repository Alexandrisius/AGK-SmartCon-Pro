using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Plans;

/// <summary>
/// Outcome classification of the plan read-back (review r1, findings 2, 6, 9
/// and 11). A well-formed path that addresses nothing in the read-back payload
/// is a mismatch (with the reason), only an unsupported path form is
/// notVerifiable; byName never falls back to a field of an object that has its
/// own identity; byId matches the row's own id key (scheduleId, materialId, …)
/// and never an id the row merely references.
/// </summary>
public sealed class ExecutePlanReadBackOutcomeTests
{
    private const string WriteArguments = @"{""items"":[{""viewId"":1}]}";

    private static AgentPlanStep Step(params AgentPlanExpectation[] expectations) => new()
    {
        StepId = "s1",
        ToolName = "write_tool",
        ArgumentsJson = WriteArguments,
        ExpectedChanges = 1,
        ReadBackTool = "read_tool",
        ArgumentsReadBackJson = "{}",
        ReadBackExpectations = expectations
    };

    private static (AgentPlanExecutor Executor, RecordingAuditLog Audit) Create(string readPayload)
    {
        var writeTool = new ScriptedTool("write_tool");
        var readTool = new ScriptedTool(
            "read_tool",
            AgentToolRisk.Read,
            supportsDryRun: false,
            handler: _ => ToolResult.Ok(Payload(readPayload), "read"),
            schema: @"{""type"":""object"",""properties"":{},""additionalProperties"":false}");
        var audit = new RecordingAuditLog();
        var executor = new AgentPlanExecutor(
            new AgentToolRegistry(new IAgentTool[] { writeTool, readTool }),
            new ImmediateAwaitableEvent(),
            new RecordingConfirmation(),
            audit);
        return (executor, audit);
    }

    private static JsonElement Payload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static Task<AgentPlanExecutionResult> Run(
        string readPayload,
        params AgentPlanExpectation[] expectations)
    {
        var (executor, _) = Create(readPayload);
        return executor.ExecuteAsync(
            new AgentPlan { Goal = "plan", Steps = new[] { Step(expectations) } },
            new AgentPlanExecutionContext { DocumentId = "doc" },
            CancellationToken.None);
    }

    // ── Finding 2: supported form, nothing found → mismatch ──────────────────

    [Fact]
    public async Task WellFormedPath_MissingRow_IsMismatch_WithReason()
    {
        // list_views answers, but the view the plan created is not among them.
        var result = await Run(
            """{"total":1,"views":[{"viewId":1,"name":"Other","templateName":null}]}""",
            new AgentPlanExpectation { Path = "views.byId[444].templateName", Expected = "T1" });

        var mismatch = Assert.Single(result.Mismatches);
        Assert.Contains("views.byId[444].templateName", mismatch, StringComparison.Ordinal);
        Assert.Contains("expected 'T1'", mismatch, StringComparison.Ordinal);
        Assert.Contains("object not found in read-back payload", mismatch, StringComparison.Ordinal);
        Assert.Empty(result.NotVerifiable);
        Assert.Equal(1, result.Steps[0].MismatchCount);
        Assert.Equal(0, result.Steps[0].NotVerifiableCount);
    }

    [Fact]
    public async Task WellFormedPath_MissingRow_IsAuditedAsMismatch()
    {
        // The audit used to say "ok" for a plan whose declared expectation was
        // not confirmed; it must record the mismatch like a differing value.
        var (executor, audit) = Create(
            """{"total":1,"views":[{"viewId":1,"name":"Other","templateName":null}]}""");
        var step = Step(new AgentPlanExpectation
        {
            Path = "views.byId[444].templateName",
            Expected = "T1"
        });

        await executor.ExecuteAsync(
            new AgentPlan { Goal = "plan", Steps = new[] { step } },
            new AgentPlanExecutionContext { DocumentId = "doc" },
            CancellationToken.None);

        var applyAudit = Assert.Single(
            audit.Entries, entry => entry.Tool == "plan#apply");
        Assert.Equal("mismatch", applyAudit.Result);
    }

    [Fact]
    public async Task MissingKey_NamesTheAvailableKeys()
    {
        // A path built against another payload shape: the model must see what
        // the read-back tool actually returned at that level.
        var result = await Run(
            """{"total":1,"items":[{"id":7,"name":"Other"}]}""",
            new AgentPlanExpectation { Path = "views[0].name", Expected = "Other" });

        var mismatch = Assert.Single(result.Mismatches);
        Assert.Contains("object not found in read-back payload", mismatch, StringComparison.Ordinal);
        Assert.Contains("'views'", mismatch, StringComparison.Ordinal);
        Assert.Contains("items", mismatch, StringComparison.Ordinal);
        Assert.Empty(result.NotVerifiable);
    }

    [Fact]
    public async Task MissingValueMapKey_IsMismatch()
    {
        // list_family_types only fills values{} for the parameter names it was
        // asked for; an expectation on a value that was not read is not
        // confirmed — a mismatch, not a silent "cannot verify".
        var result = await Run(
            """{"types":[{"typeId":11,"name":"Type A"}]}""",
            new AgentPlanExpectation { Path = "types.byId[11].values.byName[Width]", Expected = "1200 mm" });

        Assert.Single(result.Mismatches);
        Assert.Empty(result.NotVerifiable);
    }

    // ── Finding 2 (other side): unsupported form → notVerifiable ─────────────

    [Theory]
    [InlineData("views.byId[].templateName")]
    [InlineData("views.byName[].templateName")]
    [InlineData("views[].templateName")]
    [InlineData("views[Other].templateName")]
    [InlineData("views[0.templateName")]
    public async Task UnsupportedPathForm_IsNotVerifiable_NotMismatch(string path)
    {
        // Empty selectors, a non-numeric index and unbalanced brackets are
        // syntax the resolver does not understand: nothing was compared, so
        // the expectation is reported as not verifiable with the reason.
        var result = await Run(
            """{"views":[{"viewId":1,"name":"Other","templateName":"T1"}]}""",
            new AgentPlanExpectation { Path = path, Expected = "T1" });

        Assert.Empty(result.Mismatches);
        var line = Assert.Single(result.NotVerifiable);
        Assert.Contains("path form not supported", line, StringComparison.Ordinal);
        Assert.Equal(1, result.Steps[0].NotVerifiableCount);
        Assert.Equal(0, result.Steps[0].MismatchCount);
    }

    [Fact]
    public async Task NotVerifiableExpectation_DoesNotTurnTheAuditIntoMismatch()
    {
        var (executor, audit) = Create(
            """{"views":[{"viewId":1,"name":"Other","templateName":"T1"}]}""");
        var step = Step(new AgentPlanExpectation { Path = "views[Other].templateName", Expected = "T1" });

        var result = await executor.ExecuteAsync(
            new AgentPlan { Goal = "plan", Steps = new[] { step } },
            new AgentPlanExecutionContext { DocumentId = "doc" },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.NotVerifiable);
        var applyAudit = Assert.Single(audit.Entries, entry => entry.Tool == "plan#apply");
        Assert.Equal("unverified", applyAudit.Result);
    }

    // ── Finding 6: byName fallback only on objects without identity ──────────

    [Fact]
    public async Task ByName_NeverMatchesAFieldOfAnIdentifiedObject()
    {
        // "card" is one row named X that merely HAS a field keyed "Height";
        // byName[Height] must not read that field as if it were a row.
        var result = await Run(
            """
            {"cards":[{"name":"X","Height":"field-of-X"},{"name":"Height","displayValue":"true-row"}],
             "card":{"name":"X","Height":"field-of-X"}}
            """,
            new AgentPlanExpectation
            {
                Path = "cards.byName[Height].displayValue",
                Expected = "true-row"
            },
            new AgentPlanExpectation { Path = "card.byName[Height]", Expected = "field-of-X" });

        Assert.Equal(1, result.Steps[0].VerifiedCount);
        var mismatch = Assert.Single(result.Mismatches);
        Assert.Contains("card.byName[Height]", mismatch, StringComparison.Ordinal);
        Assert.Empty(result.NotVerifiable);
    }

    [Fact]
    public async Task ByName_StillAddressesFlatValueMaps()
    {
        // The fallback exists for dictionaries keyed by parameter name.
        var result = await Run(
            """{"types":[{"typeId":11,"values":{"Width":"1200 mm","Mark":"W.1"}}]}""",
            new AgentPlanExpectation { Path = "types.byId[11].values.byName[Width]", Expected = "1200 mm" },
            new AgentPlanExpectation { Path = "types.byId[11].values.byName[Mark]", Expected = "W.1" });

        Assert.Empty(result.Mismatches);
        Assert.Empty(result.NotVerifiable);
        Assert.Equal(2, result.Steps[0].VerifiedCount);
    }

    // ── Finding 9: byId matches the row's own id key ─────────────────────────

    [Fact]
    public async Task ById_ResolvesTheRowsOwnIdKey_OfEveryCollection()
    {
        // list_schedules / list_materials / list_levels identify their rows by
        // scheduleId / materialId / levelId; "byId works for any row with an
        // id" must hold for all of them, not only for the id/viewId keys.
        var result = await Run(
            """
            {"schedules":[{"scheduleId":501,"name":"Report A","fieldCount":3},
                          {"scheduleId":502,"name":"Report B","fieldCount":0}],
             "materials":[{"materialId":61,"name":"Steel"}],
             "levels":[{"levelId":71,"name":"Level 1"}],
             "sheets":[{"id":81,"number":"A-1"}]}
            """,
            new AgentPlanExpectation { Path = "schedules.byId[502].fieldCount", Expected = "0" },
            new AgentPlanExpectation { Path = "materials.byId[61].name", Expected = "Steel" },
            new AgentPlanExpectation { Path = "levels.byId[71].name", Expected = "Level 1" },
            new AgentPlanExpectation { Path = "sheets.byId[81].number", Expected = "A-1" });

        Assert.Equal(4, result.Steps[0].VerifiedCount);
        Assert.Empty(result.Mismatches);
        Assert.Empty(result.NotVerifiable);
    }

    [Fact]
    public async Task ById_NeverMatchesAnIdTheRowOnlyReferences()
    {
        // 200 is the view's template, 100 its level: neither identifies the
        // row, so neither may resolve to it (it used to return the view).
        var result = await Run(
            """{"views":[{"viewId":1,"name":"View A","templateId":200,"levelId":100}]}""",
            new AgentPlanExpectation { Path = "views.byId[200].templateName", Expected = "T" },
            new AgentPlanExpectation { Path = "views.byId[100].name", Expected = "View A" });

        Assert.Equal(0, result.Steps[0].VerifiedCount);
        Assert.Equal(2, result.Mismatches.Count);
        Assert.All(result.Mismatches,
            line => Assert.Contains("object not found in read-back payload", line, StringComparison.Ordinal));
        Assert.Empty(result.NotVerifiable);
    }

    [Fact]
    public async Task ById_StillSearchesRowsNestedUnderObjects()
    {
        // A path may start with the selector: the row is found wherever the
        // payload nests it, as before.
        var result = await Run(
            """{"data":{"views":[{"viewId":2,"name":"Target","templateName":"T"}]}}""",
            new AgentPlanExpectation { Path = "byId[2].templateName", Expected = "T" });

        Assert.Equal(1, result.Steps[0].VerifiedCount);
        Assert.Empty(result.Mismatches);
    }
}
