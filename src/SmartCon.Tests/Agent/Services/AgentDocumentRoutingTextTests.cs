using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// documentId routing argument (night run 2, C9-01/C9-02/C9-08): the tolerant
/// repair never deletes an undeclared documentId, the validator turns it into
/// a typed invalid_arguments error before any tool runs, and the documents and
/// families skills describe the tools as they really behave.
/// </summary>
public sealed class AgentDocumentRoutingTextTests
{
    private const string StrictSchema =
        """{"type":"object","properties":{"viewId":{"type":"integer"}},"additionalProperties":false}""";

    private const string LooseSchema =
        """{"type":"object","properties":{"viewId":{"type":"integer"}}}""";

    private const string RoutedSchema =
        """{"type":"object","properties":{"documentId":{"type":"string"},"viewId":{"type":"integer"}},"additionalProperties":false}""";

    private static readonly string[] SessionTools =
    [
        "get_selected_elements", "get_current_view_info", "list_change_sets",
        "get_change_set_details", "list_shared_parameters"
    ];

    [Fact]
    public void Repair_KeepsUndeclaredDocumentId_DropsOtherUnknownProperties()
    {
        using var arguments = JsonDocument.Parse("""{"documentId":"donor","bogus":1,"view_id":7}""");

        var repaired = AgentToolInputRepair.Repair(StrictSchema, arguments.RootElement);

        Assert.True(repaired.Changed);
        Assert.Equal("donor", repaired.Arguments.GetProperty("documentId").GetString());
        Assert.False(repaired.Arguments.TryGetProperty("bogus", out _));
        Assert.Equal(7, repaired.Arguments.GetProperty("viewId").GetInt32());
        Assert.Contains(repaired.Repairs, repair => repair.Contains("dropped unknown property 'bogus'", StringComparison.Ordinal));
        Assert.DoesNotContain(repaired.Repairs, repair => repair.Contains("documentId", StringComparison.Ordinal));
    }

    [Fact]
    public void Repair_DocumentIdAlone_IsNotARepairSoTheValidatorStillRejectsIt()
    {
        using var arguments = JsonDocument.Parse("""{"viewId":7,"documentId":"donor"}""");

        var repaired = AgentToolInputRepair.Repair(StrictSchema, arguments.RootElement);

        Assert.False(repaired.Changed);
        Assert.Equal("donor", repaired.Arguments.GetProperty("documentId").GetString());
        Assert.Empty(repaired.Repairs);
    }

    [Fact]
    public void Validator_FlagsUndeclaredDocumentId_WhateverAdditionalPropertiesSays()
    {
        foreach (var schema in new[] { StrictSchema, LooseSchema, "{}", "" })
        {
            var errors = AgentToolSchemaValidator.Validate(schema, """{"viewId":7,"documentId":"donor"}""");
            var error = Assert.Single(errors);
            Assert.Equal("$.documentId: " + AgentDocumentRoutingArgument.UndeclaredError, error);
        }
    }

    [Fact]
    public void Validator_DeclaredOrAbsentDocumentId_IsNotFlagged()
    {
        Assert.Empty(AgentToolSchemaValidator.Validate(RoutedSchema, """{"viewId":7,"documentId":"donor"}"""));
        Assert.Empty(AgentToolSchemaValidator.Validate(StrictSchema, """{"viewId":7}"""));

        // An ordinary unknown property keeps its generic message.
        Assert.Equal(
            "$.bogus: unknown property",
            Assert.Single(AgentToolSchemaValidator.Validate(StrictSchema, """{"bogus":1}""")));
    }

    [Fact]
    public void UndeclaredErrorText_IsAccurateForWriteAndSessionTools()
    {
        // Review r1 P3: "does not read documents by documentId" misled for
        // write tools (they are not supposed to read); the accurate contract
        // is that the tool does not accept the argument at all.
        Assert.Equal(
            "unknown property - this tool does not accept documentId; omit it",
            AgentDocumentRoutingArgument.UndeclaredError);
    }

    [Fact]
    public async Task Executor_DocumentIdForToolWithoutIt_FailsTypedBeforeTheToolRuns()
    {
        var tool = new ScriptedTool(
            "list_change_sets", AgentToolRisk.Read, supportsDryRun: false, schema: StrictSchema);
        var executor = CreateExecutor(tool);

        var result = await executor.ExecuteAsync(
            "list_change_sets",
            Args("""{"viewId":7,"documentId":"donor"}"""),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("invalid_arguments", result.ErrorCode);
        Assert.Equal(
            "Invalid arguments for tool 'list_change_sets': $.documentId: " +
            AgentDocumentRoutingArgument.UndeclaredError +
            ". [Action: fix the listed paths; see the tool schema]",
            result.Message);
        Assert.Empty(tool.Executions);
    }

    [Fact]
    public async Task Executor_DocumentIdForRoutedTool_ReachesTheTool()
    {
        var tool = new ScriptedTool(
            "list_views", AgentToolRisk.Read, supportsDryRun: false, schema: RoutedSchema);
        var executor = CreateExecutor(tool);

        var result = await executor.ExecuteAsync(
            "list_views",
            Args("""{"viewId":7,"documentId":"donor"}"""),
            CancellationToken.None);

        Assert.True(result.Success);
        var execution = Assert.Single(tool.Executions);
        Assert.Contains("\"documentId\":\"donor\"", execution.ArgumentsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentsSkill_NoUniversalClaim_ListsSessionToolsAndTheRoutingFact()
    {
        var skill = AgentToolPackSkills.TryGet("documents");
        Assert.NotNull(skill);

        Assert.DoesNotContain("Все read-туры принимают", skill, StringComparison.Ordinal);
        foreach (var name in SessionTools)
            Assert.Contains("`" + name + "`", skill, StringComparison.Ordinal);
        Assert.Contains("даёт `invalid_arguments`", skill, StringComparison.Ordinal);
        Assert.Contains("поле `document` (`scope: active|background`, `documentId`)", skill, StringComparison.Ordinal);
        Assert.Contains("documentId was not present", skill, StringComparison.Ordinal);

        // classify_types routes by its own code and attaches neither the field nor the NOTE.
        Assert.Contains("Исключение — `classify_types`", skill, StringComparison.Ordinal);
    }

    [Fact]
    public void FamiliesSkill_ProjectSharedParameterIsNotInGetFamilyParameters()
    {
        var skill = AgentToolPackSkills.TryGet("families");
        Assert.NotNull(skill);

        Assert.Contains("документ семейства** (FamilyManager)", skill, StringComparison.Ordinal);
        Assert.Contains("Проектный общий параметр", skill, StringComparison.Ordinal);
        Assert.Contains("list_family_types(familyId, parameterNames", skill, StringComparison.Ordinal);
    }

    private static AgentToolExecutor CreateExecutor(params IAgentTool[] tools) => new(
        new AgentToolRegistry(tools),
        new ImmediateAwaitableEvent(),
        new AgentSessionContext { DocumentId = "doc" },
        new RecordingConfirmation());

    private static JsonElement Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
