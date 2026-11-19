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
        AgentApprovalMode mode)
    {
        var registry = new AgentToolRegistry(new[] { tool });
        return new AgentToolExecutor(
            registry,
            new ImmediateAwaitableEvent(),
            new AgentSessionContext { DocumentId = "doc", ApprovalMode = mode },
            confirmation,
            audit);
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
    public async Task AutoApprove_DestructiveRisk_AppliesWithoutDialog()
    {
        var tool = new ScriptedTool("danger_tool", AgentToolRisk.Destructive);
        var confirmation = new RecordingConfirmation();
        var audit = new RecordingAuditLog();
        var executor = CreateExecutor(
            tool, confirmation, audit, AgentApprovalMode.AutoApproveSession);

        var result = await executor.ExecuteAsync(
            "danger_tool", Args(@"{""items"":[{""viewId"":1}],""dryRun"":false}"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(confirmation.Requests);
        Assert.Contains(audit.Entries, entry =>
            entry.Tool == "danger_tool#auto_approved");
    }

    [Fact]
    public async Task AutoApprove_AboveAffectedThreshold_AppliesWithoutDialog()
    {
        var tool = new ScriptedTool("write_tool");
        var confirmation = new RecordingConfirmation();
        var audit = new RecordingAuditLog();
        var executor = CreateExecutor(
            tool, confirmation, audit, AgentApprovalMode.AutoApproveSession);
        var items = string.Join(",", Enumerable.Range(0, 500)
            .Select(index => "{\"viewId\":" + index + "}"));
        var args = Args("{\"items\":[" + items + "],\"dryRun\":false}");

        var result = await executor.ExecuteAsync("write_tool", args, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(confirmation.Requests);
        Assert.Contains(audit.Entries, entry =>
            entry.Tool == "write_tool#auto_approved"
            && entry.Result == "auto_approved:bypass");
    }

    [Fact]
    public async Task AutoApprove_DiscardChangeSet_AppliesWithoutDialog()
    {
        var tool = new ScriptedTool("discard_change_set", supportsDryRun: false);
        var confirmation = new RecordingConfirmation();
        var executor = CreateExecutor(
            tool, confirmation, new RecordingAuditLog(), AgentApprovalMode.AutoApproveSession);

        var result = await executor.ExecuteAsync(
            "discard_change_set", Args("{}"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(confirmation.Requests);
    }

    [Fact]
    public async Task AutoApprove_DestructivePlan_AppliesWithoutDialog()
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

        var result = await executor.ExecuteAsync("execute_plan", args, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(confirmation.Requests);
    }

    [Fact]
    public async Task AutoApprove_ManyConfigureViewsSections_AppliesWithoutDialog()
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

        Assert.Empty(confirmation.Requests);
    }    [Fact]
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
