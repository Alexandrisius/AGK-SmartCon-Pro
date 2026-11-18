using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Plans;

public sealed class AgentPlanValidatorTests
{
    private static AgentToolDefinition Tool(
        string name,
        AgentToolRisk risk = AgentToolRisk.Write,
        string? schema = null) =>
        new()
        {
            Name = name,
            Description = name,
            Risk = risk,
            SupportsDryRun = risk != AgentToolRisk.Read,
            InputSchemaJson = schema
                              ?? @"{""type"":""object"",""properties"":{""items"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""}},""required"":[""viewId""],""additionalProperties"":false}}},""required"":[""items""],""additionalProperties"":false}"
        };

    private static AgentPlanStep Step(
        string id,
        string tool = "write_tool",
        string args = @"{""items"":[{""viewId"":1}]}",
        params string[] dependsOn) =>
        new()
        {
            StepId = id,
            ToolName = tool,
            ArgumentsJson = args,
            DependsOn = dependsOn,
            ExpectedChanges = 1,
            ReadBackTool = "read_tool",
            ReadBackExpectations = new[] { new AgentPlanExpectation { Path = "name", Expected = "x" } }
        };

    private static AgentPlan Plan(params AgentPlanStep[] steps) =>
        new() { Goal = "test goal", Steps = steps };

    private static readonly IReadOnlyList<AgentToolDefinition> Tools = new[]
    {
        Tool("write_tool"),
        Tool("read_tool", AgentToolRisk.Read, @"{""type"":""object"",""properties"":{}}"),
        Tool("execute_plan"),
        Tool("destructive_tool", AgentToolRisk.Destructive)
    };

    [Fact]
    public void MissingPlan_IsRejected()
    {
        var result = AgentPlanValidator.Validate(null!, Tools);

        Assert.Contains(result.Errors, error => error.Code == "plan_missing");
    }

    [Fact]
    public void EmptyGoal_IsRejected()
    {
        var result = AgentPlanValidator.Validate(
            new AgentPlan { Goal = " ", Steps = new[] { Step("s1") } },
            Tools);

        Assert.Contains(result.Errors, error => error.Code == "plan_goal_missing");
    }

    [Fact]
    public void MissingStepId_IsRejected()
    {
        var result = AgentPlanValidator.Validate(Plan(Step("")), Tools);

        Assert.Contains(result.Errors, error => error.Code == "plan_step_id_missing");
    }

    [Fact]
    public void NestedPlanStep_IsRejected()
    {
        foreach (var name in new[] { "execute_plan", "Execute_Plan" })
        {
            var result = AgentPlanValidator.Validate(Plan(Step("s1", name)), Tools);

            Assert.Contains(
                result.Errors, error => error.Code == "plan_nested_plan_forbidden");
        }
    }

    [Fact]
    public void ValidPlan_HasNoErrors()
    {
        var result = AgentPlanValidator.Validate(Plan(Step("s1"), Step("s2", dependsOn: "s1")), Tools);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void UnknownTool_IsRejected()
    {
        var result = AgentPlanValidator.Validate(Plan(Step("s1", "nope")), Tools);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "plan_tool_unknown");
    }

    [Fact]
    public void EmptySteps_IsRejected()
    {
        var result = AgentPlanValidator.Validate(Plan(), Tools);

        Assert.Contains(result.Errors, error => error.Code == "plan_empty");
    }

    [Fact]
    public void TooManySteps_IsRejected()
    {
        var steps = Enumerable.Range(0, AgentPlanValidator.MaxSteps + 1)
            .Select(index => Step("s" + index))
            .ToArray();

        var result = AgentPlanValidator.Validate(Plan(steps), Tools);

        Assert.Contains(result.Errors, error => error.Code == "plan_too_large");
    }

    [Fact]
    public void DuplicateStepId_IsRejected()
    {
        var result = AgentPlanValidator.Validate(Plan(Step("s1"), Step("s1")), Tools);

        Assert.Contains(result.Errors, error => error.Code == "plan_duplicate_step");
    }

    [Fact]
    public void InvalidArguments_AreRejected()
    {
        var result = AgentPlanValidator.Validate(
            Plan(Step("s1", args: @"{""items"":[],""unexpected"":1}")),
            Tools);

        Assert.Contains(result.Errors, error => error.Code == "plan_args_invalid");
    }

    [Fact]
    public void MissingRequiredArgument_IsRejected()
    {
        var result = AgentPlanValidator.Validate(Plan(Step("s1", args: "{}")), Tools);

        Assert.Contains(result.Errors, error => error.Code == "plan_args_invalid");
    }

    [Fact]
    public void UnknownDependency_IsRejected()
    {
        var result = AgentPlanValidator.Validate(Plan(Step("s1", dependsOn: "ghost")), Tools);

        Assert.Contains(result.Errors, error => error.Code == "plan_dependency_unknown");
    }

    [Fact]
    public void DependencyCycle_IsRejected()
    {
        var result = AgentPlanValidator.Validate(
            Plan(Step("s1", dependsOn: "s2"), Step("s2", dependsOn: "s1")),
            Tools);

        Assert.Contains(result.Errors, error => error.Code == "plan_cycle");
    }

    [Fact]
    public void SelfDependency_IsRejected()
    {
        var result = AgentPlanValidator.Validate(Plan(Step("s1", dependsOn: "s1")), Tools);

        Assert.Contains(result.Errors, error => error.Code == "plan_cycle");
    }

    [Fact]
    public void DestructiveTool_RequiresExplicitOptIn()
    {
        var plain = AgentPlanValidator.Validate(Plan(Step("s1", "destructive_tool")), Tools);
        Assert.Contains(plain.Errors, error => error.Code == "plan_destructive_forbidden");

        var allowed = AgentPlanValidator.Validate(
            new AgentPlan
            {
                Goal = "goal",
                AllowDestructive = true,
                Steps = new[] { Step("s1", "destructive_tool") }
            },
            Tools);
        Assert.DoesNotContain(allowed.Errors, error => error.Code == "plan_destructive_forbidden");
    }

    [Fact]
    public void MissingReadBack_IsAWarningNotAnError()
    {
        var step = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = @"{""items"":[{""viewId"":1}]}",
            ExpectedChanges = 1
        };

        var result = AgentPlanValidator.Validate(Plan(step), Tools);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, warning => warning.Code == "plan_no_readback");
    }

    [Fact]
    public void OrderSteps_PutsDependenciesFirst()
    {
        var plan = Plan(Step("s2", dependsOn: "s1"), Step("s1"), Step("s3", dependsOn: "s2"));

        var order = AgentPlanValidator.OrderSteps(plan);

        Assert.Equal(new[] { "s1", "s2", "s3" }, order);
    }
}
