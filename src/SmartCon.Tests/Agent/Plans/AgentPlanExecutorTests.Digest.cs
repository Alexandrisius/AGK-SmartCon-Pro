using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Plans;

/// <summary>Digest computation of a plan: what the preview binds to the later apply.</summary>
public sealed partial class AgentPlanExecutorTests
{
    [Fact]
    public void ComputeDigest_IsWhitespaceAndOrderInsensitive_ButDocumentBound()
    {
        var stepA = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = @"{""items"":[{""viewId"":1}]}",
            DependsOn = new[] { "b", "a" },
            ReadBackExpectations = new[]
            {
                new AgentPlanExpectation { Path = "name", Expected = "x" },
                new AgentPlanExpectation { Path = "id", Expected = "1" }
            }
        };
        var stepB = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = "{ \"items\" : [ { \"viewId\" : 1 } ] }",
            DependsOn = new[] { "a", "b" },
            ReadBackExpectations = new[]
            {
                new AgentPlanExpectation { Path = "id", Expected = "1" },
                new AgentPlanExpectation { Path = "name", Expected = "x" }
            }
        };

        var digestA = AgentPlanExecutor.ComputeDigest(Plan(stepA), "doc-1");
        var digestB = AgentPlanExecutor.ComputeDigest(Plan(stepB), "doc-1");
        var digestOtherDocument = AgentPlanExecutor.ComputeDigest(Plan(stepA), "doc-2");

        Assert.Equal(digestA, digestB);
        Assert.NotEqual(digestA, digestOtherDocument);
    }

    [Fact]
    public void ComputeDigest_IgnoresDryRunFlags()
    {
        var stepA = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = @"{""items"":[{""viewId"":1}],""dryRun"":true}",
            DryRun = true
        };
        var stepB = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = @"{""items"":[{""viewId"":1}],""dryRun"":false}",
            DryRun = false
        };

        var digestA = AgentPlanExecutor.ComputeDigest(Plan(stepA), "doc-1");
        var digestB = AgentPlanExecutor.ComputeDigest(Plan(stepB), "doc-1");

        Assert.Equal(digestA, digestB);
    }

    [Fact]
    public void ComputeDigest_IgnoresReadBackFields()
    {
        var withoutReadBack = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = @"{""items"":[{""viewId"":1}]}"
        };
        var withReadBack = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = @"{""items"":[{""viewId"":1}]}",
            ReadBackTool = "read_tool",
            ArgumentsReadBackJson = @"{""viewId"":1}",
            ReadBackExpectations = new[]
            {
                new AgentPlanExpectation { Path = "name", Expected = "x" }
            }
        };

        Assert.Equal(
            AgentPlanExecutor.ComputeDigest(Plan(withoutReadBack), "doc-1"),
            AgentPlanExecutor.ComputeDigest(Plan(withReadBack), "doc-1"));
    }

    [Fact]
    public void ComputeDigest_IgnoresGoalText()
    {
        // Benchmark run 2, task 5: rewording the goal between preview and apply
        // produced plan_digest_mismatch although the executable steps were identical.
        var step = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = @"{""items"":[{""viewId"":1}]}"
        };
        var first = new AgentPlan { Goal = "Переименовать виды", Steps = new[] { step } };
        var second = new AgentPlan
        {
            Goal = "Привести имена видов к схеме разделов",
            Steps = new[] { step }
        };

        Assert.Equal(
            AgentPlanExecutor.ComputeDigest(first, "doc-1"),
            AgentPlanExecutor.ComputeDigest(second, "doc-1"));
    }

    [Fact]
    public void ConfirmationLedger_ConsumesNonceOnce()
    {
        var ledger = new AgentConfirmationLedger();

        Assert.True(ledger.TryConsume("nonce"));
        Assert.False(ledger.TryConsume("nonce"));
        Assert.True(ledger.IsConsumed("nonce"));
        Assert.False(ledger.TryConsume(null));
        Assert.False(ledger.TryConsume(string.Empty));
    }
}
