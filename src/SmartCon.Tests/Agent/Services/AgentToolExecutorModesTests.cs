using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentToolExecutorModesTests
{
    private static JsonElement Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static AgentToolExecutor CreateExecutor(
        IAgentTool tool,
        RecordingConfirmation confirmation,
        RecordingAuditLog audit,
        AgentApprovalMode mode,
        IActionRiskGate? riskGate = null)
    {
        var registry = new AgentToolRegistry(new[] { tool });
        return new AgentToolExecutor(
            registry,
            new ImmediateAwaitableEvent(),
            new AgentSessionContext { DocumentId = "doc", ApprovalMode = mode },
            confirmation,
            audit,
            riskGate);
    }

    [Fact]
    public async Task ReadOnly_BlocksWrite_AndAllowsRead()
    {
        var write = new ScriptedTool("write_tool");
        var read = new ScriptedTool("read_tool", AgentToolRisk.Read, supportsDryRun: false);
        var confirmation = new RecordingConfirmation();
        var registry = new AgentToolRegistry(new IAgentTool[] { write, read });
        var executor = new AgentToolExecutor(
            registry,
            new ImmediateAwaitableEvent(),
            new AgentSessionContext { DocumentId = "doc", ApprovalMode = AgentApprovalMode.ReadOnly },
            confirmation,
            new RecordingAuditLog());

        var writeResult = await executor.ExecuteAsync(
            "write_tool", Args(@"{""items"":[{""viewId"":1}]}"), CancellationToken.None);
        var readResult = await executor.ExecuteAsync(
            "read_tool", Args("{}"), CancellationToken.None);

        Assert.False(writeResult.Success);
        Assert.Equal("read_only_mode", writeResult.ErrorCode);
        Assert.Empty(write.Executions);
        Assert.True(readResult.Success);
        Assert.Single(read.Executions);
    }

    [Fact]
    public async Task AutoApprove_SkipsDialog_AndAuditsBypass()
    {
        var tool = new ScriptedTool("write_tool");
        var confirmation = new RecordingConfirmation { Result = AgentConfirmationResult.Rejected };
        var audit = new RecordingAuditLog();
        var executor = CreateExecutor(
            tool, confirmation, audit, AgentApprovalMode.AutoApproveSession);

        var result = await executor.ExecuteAsync(
            "write_tool", Args(@"{""items"":[{""viewId"":1}],""dryRun"":false}"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(confirmation.Requests);
        Assert.True(tool.Executions.Single().Approved);
        Assert.Contains(audit.Entries, entry =>
            entry.Tool == "write_tool#auto_approved"
            && entry.Result == "auto_approved:bypass");
    }

    [Fact]
    public async Task AutoApprove_DestructiveRisk_StillConfirms()
    {
        var tool = new ScriptedTool("danger_tool", AgentToolRisk.Destructive);
        var confirmation = new RecordingConfirmation();
        var audit = new RecordingAuditLog();
        var executor = CreateExecutor(
            tool, confirmation, audit, AgentApprovalMode.AutoApproveSession);

        await executor.ExecuteAsync(
            "danger_tool", Args(@"{""items"":[{""viewId"":1}],""dryRun"":false}"),
            CancellationToken.None);

        Assert.Single(confirmation.Requests);
        Assert.DoesNotContain(audit.Entries, entry =>
            entry.Tool == "danger_tool#auto_approved");
    }

    [Fact]
    public async Task AutoApprove_AboveAffectedThreshold_StillConfirms()
    {
        var tool = new ScriptedTool("write_tool");
        var confirmation = new RecordingConfirmation();
        var audit = new RecordingAuditLog();
        var executor = CreateExecutor(
            tool, confirmation, audit, AgentApprovalMode.AutoApproveSession);
        var items = string.Join(",", Enumerable.Range(0, 201)
            .Select(index => "{\"viewId\":" + index + "}"));
        var args = Args("{\"items\":[" + items + "],\"dryRun\":false}");

        await executor.ExecuteAsync("write_tool", args, CancellationToken.None);

        Assert.Single(confirmation.Requests);
        Assert.Contains(confirmation.Requests, request => request.AffectedCount > 200);
    }

    [Fact]
    public async Task AutoApprove_RiskGateAuto_SkipsDialog()
    {
        var tool = new ScriptedTool("write_tool");
        var confirmation = new RecordingConfirmation();
        var audit = new RecordingAuditLog();
        var gate = new FakeRiskGate { Decision = AgentRiskDecision.Auto };
        var executor = CreateExecutor(
            tool, confirmation, audit, AgentApprovalMode.AutoApproveSession, gate);

        var result = await executor.ExecuteAsync(
            "write_tool", Args(@"{""items"":[{""viewId"":1}],""dryRun"":false}"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(confirmation.Requests);
        Assert.Single(gate.Requests);
        Assert.Contains(audit.Entries, entry =>
            entry.Tool == "write_tool#auto_approved"
            && entry.Result == "auto_approved:risk_gate");
    }

    [Fact]
    public async Task AutoApprove_RiskGateThrows_FailsClosedToDialog()
    {
        var tool = new ScriptedTool("write_tool");
        var confirmation = new RecordingConfirmation();
        var gate = new FakeRiskGate { Throw = true };
        var executor = CreateExecutor(
            tool, confirmation, new RecordingAuditLog(),
            AgentApprovalMode.AutoApproveSession, gate);

        await executor.ExecuteAsync(
            "write_tool", Args(@"{""items"":[{""viewId"":1}],""dryRun"":false}"),
            CancellationToken.None);

        Assert.Single(confirmation.Requests);
    }

    [Fact]
    public async Task AutoApprove_NestedPlanAboveThreshold_StillConfirms()
    {
        var nested = new ScriptedTool("write_tool");
        var planTool = new ScriptedTool("execute_plan", schema: "{}");
        var registry = new AgentToolRegistry(new IAgentTool[] { nested, planTool });
        var confirmation = new RecordingConfirmation();
        var executor = new AgentToolExecutor(
            registry,
            new ImmediateAwaitableEvent(),
            new AgentSessionContext { DocumentId = "doc", ApprovalMode = AgentApprovalMode.AutoApproveSession },
            confirmation,
            new RecordingAuditLog());
        var items = string.Join(",", Enumerable.Range(0, 150)
            .Select(index => "{\"viewId\":" + index + "}"));
        var args = Args(
            "{\"goal\":\"big\",\"dryRun\":false,\"steps\":[" +
            "{\"stepId\":\"s1\",\"toolName\":\"write_tool\",\"arguments\":{\"items\":[" + items + "]}}," +
            "{\"stepId\":\"s2\",\"toolName\":\"write_tool\",\"arguments\":{\"items\":[" + items + "]}}]}");

        await executor.ExecuteAsync("execute_plan", args, CancellationToken.None);

        Assert.Single(confirmation.Requests);
        Assert.Contains(confirmation.Requests, request => request.AffectedCount > 200);
    }

    [Fact]
    public async Task AutoApprove_NestedDestructiveStep_StillConfirms()
    {
        var destructive = new ScriptedTool("danger_tool", AgentToolRisk.Destructive);
        var planTool = new ScriptedTool("execute_plan", schema: "{}");
        var registry = new AgentToolRegistry(new IAgentTool[] { destructive, planTool });
        var confirmation = new RecordingConfirmation();
        var executor = new AgentToolExecutor(
            registry,
            new ImmediateAwaitableEvent(),
            new AgentSessionContext { DocumentId = "doc", ApprovalMode = AgentApprovalMode.AutoApproveSession },
            confirmation,
            new RecordingAuditLog());
        var args = Args(
            "{\"goal\":\"danger\",\"dryRun\":false,\"steps\":[" +
            "{\"stepId\":\"s1\",\"toolName\":\"danger_tool\",\"arguments\":{\"items\":[{\"viewId\":1}]}}]}");

        await executor.ExecuteAsync("execute_plan", args, CancellationToken.None);

        Assert.Single(confirmation.Requests);
    }

    [Fact]
    public async Task AutoApprove_ConfigureViewsManySections_StillConfirms()
    {
        var tool = new ScriptedTool("configure_views");
        var confirmation = new RecordingConfirmation();
        var executor = CreateExecutor(
            tool, confirmation, new RecordingAuditLog(), AgentApprovalMode.AutoApproveSession);
        var items = string.Join(",", Enumerable.Range(0, 100).Select(index =>
            "{\"viewId\":" + index +
            ",\"newName\":\"V" + index + "\",\"crop\":{\"visible\":true},\"visibility\":{\"categories\":[]}}"));
        var args = Args("{\"items\":[" + items + "],\"dryRun\":false}");

        await executor.ExecuteAsync("configure_views", args, CancellationToken.None);

        Assert.Single(confirmation.Requests);
        Assert.Contains(confirmation.Requests, request => request.AffectedCount > 200);
    }

    [Fact]
    public async Task AutoApprove_SetVisibilityManyCategories_StillConfirms()
    {
        var tool = new ScriptedTool("set_view_visibility");
        var confirmation = new RecordingConfirmation();
        var executor = CreateExecutor(
            tool, confirmation, new RecordingAuditLog(), AgentApprovalMode.AutoApproveSession);
        var items = string.Join(",", Enumerable.Range(0, 100).Select(index =>
            "{\"viewId\":" + index +
            ",\"visibility\":{\"categories\":[1,2,3],\"worksets\":[]}}"));
        var args = Args("{\"items\":[" + items + "],\"dryRun\":false}");

        await executor.ExecuteAsync("set_view_visibility", args, CancellationToken.None);

        Assert.Single(confirmation.Requests);
        Assert.Contains(confirmation.Requests, request => request.AffectedCount > 200);
    }

    [Fact]
    public async Task AutoApprove_CopySettingsAllSections_StillConfirms()
    {
        var tool = new ScriptedTool("copy_view_settings", schema: "{}");
        var confirmation = new RecordingConfirmation();
        var executor = CreateExecutor(
            tool, confirmation, new RecordingAuditLog(), AgentApprovalMode.AutoApproveSession);
        var targets = string.Join(",", Enumerable.Range(0, 100));
        var args = Args(
            "{\"sourceViewId\":0,\"targetViewIds\":[" + targets +
            "],\"sections\":[\"all\"],\"dryRun\":false}");

        await executor.ExecuteAsync("copy_view_settings", args, CancellationToken.None);

        Assert.Single(confirmation.Requests);
        Assert.Contains(confirmation.Requests, request => request.AffectedCount > 200);
    }

    [Fact]
    public async Task AutoApprove_RemapWithoutLimit_StillConfirms()
    {
        var tool = new ScriptedTool("remap_parameter_values", schema: "{}");
        var confirmation = new RecordingConfirmation();
        var executor = CreateExecutor(
            tool, confirmation, new RecordingAuditLog(), AgentApprovalMode.AutoApproveSession);
        var args = Args(
            "{\"mapping\":[{\"target\":{\"name\":\"Тип\"}}],\"dryRun\":false}");

        await executor.ExecuteAsync("remap_parameter_values", args, CancellationToken.None);

        Assert.Single(confirmation.Requests);
        Assert.Contains(confirmation.Requests, request => request.AffectedCount > 200);
    }

    [Fact]
    public async Task AutoApprove_DiscardChangeSet_StillConfirms()
    {
        var tool = new ScriptedTool("discard_change_set", supportsDryRun: false);
        var confirmation = new RecordingConfirmation();
        var executor = CreateExecutor(
            tool, confirmation, new RecordingAuditLog(), AgentApprovalMode.AutoApproveSession);

        await executor.ExecuteAsync("discard_change_set", Args("{}"), CancellationToken.None);

        Assert.Single(confirmation.Requests);
    }

    [Fact]
    public async Task AutoApprove_NestedDiscardStep_StillConfirms()
    {
        var discard = new ScriptedTool("discard_change_set", supportsDryRun: false);
        var planTool = new ScriptedTool("execute_plan", schema: "{}");
        var registry = new AgentToolRegistry(new IAgentTool[] { discard, planTool });
        var confirmation = new RecordingConfirmation();
        var executor = new AgentToolExecutor(
            registry,
            new ImmediateAwaitableEvent(),
            new AgentSessionContext { DocumentId = "doc", ApprovalMode = AgentApprovalMode.AutoApproveSession },
            confirmation,
            new RecordingAuditLog());
        var args = Args(
            "{\"goal\":\"close old set\",\"dryRun\":false,\"steps\":[" +
            "{\"stepId\":\"s1\",\"toolName\":\"discard_change_set\",\"arguments\":{}}]}");

        await executor.ExecuteAsync("execute_plan", args, CancellationToken.None);

        Assert.Single(confirmation.Requests);
    }

    [Fact]
    public async Task AutoApprove_DestructivePlan_ReportsConservativeAffectedCount()
    {
        var destructive = new ScriptedTool("danger_tool", AgentToolRisk.Destructive);
        var planTool = new ScriptedTool("execute_plan", schema: "{}");
        var registry = new AgentToolRegistry(new IAgentTool[] { destructive, planTool });
        var confirmation = new RecordingConfirmation();
        var executor = new AgentToolExecutor(
            registry,
            new ImmediateAwaitableEvent(),
            new AgentSessionContext { DocumentId = "doc", ApprovalMode = AgentApprovalMode.AutoApproveSession },
            confirmation,
            new RecordingAuditLog());
        var args = Args(
            "{\"goal\":\"rebuild\",\"allowDestructive\":true,\"dryRun\":false,\"steps\":[" +
            "{\"stepId\":\"s1\",\"toolName\":\"danger_tool\",\"expectedChanges\":300," +
            "\"arguments\":{\"items\":[{\"viewId\":1}]}}]}");

        await executor.ExecuteAsync("execute_plan", args, CancellationToken.None);

        Assert.Single(confirmation.Requests);
        Assert.Contains(confirmation.Requests, request => request.AffectedCount >= 300);
        Assert.Contains(confirmation.Requests[0].Risks, risk => risk.Contains("destructive"));
    }

    [Fact]
    public async Task ConfirmWrites_Mode_StillAsksTheUser()
    {
        var tool = new ScriptedTool("write_tool");
        var confirmation = new RecordingConfirmation();
        var executor = CreateExecutor(
            tool, confirmation, new RecordingAuditLog(), AgentApprovalMode.ConfirmWrites);

        await executor.ExecuteAsync(
            "write_tool", Args(@"{""items"":[{""viewId"":1}],""dryRun"":false}"),
            CancellationToken.None);

        Assert.Single(confirmation.Requests);
    }

    [Fact]
    public async Task DryRun_DoesNotAskInAnyMode()
    {
        var tool = new ScriptedTool("write_tool");
        var confirmation = new RecordingConfirmation();
        var executor = CreateExecutor(
            tool, confirmation, new RecordingAuditLog(), AgentApprovalMode.ConfirmWrites);

        await executor.ExecuteAsync(
            "write_tool", Args(@"{""items"":[{""viewId"":1}]}"), CancellationToken.None);

        Assert.Empty(confirmation.Requests);
    }
}
