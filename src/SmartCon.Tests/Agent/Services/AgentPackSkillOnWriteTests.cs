using System.Text.Json;
using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// A write tool can be callable before its pack was loaded (the initial shortlist
/// ranks single tools and carries no skill). Its first result must deliver the pack
/// manual, otherwise the model writes without reading it (#400).
/// </summary>
public sealed class AgentPackSkillOnWriteTests
{
    private static AgentOrchestrator CreateHarness(
        AgentSessionContext session,
        FakeAgentLlmClient llm,
        params IAgentTool[] tools)
    {
        var loadTool = new LoadToolPackTool(session);
        var registry = new AgentToolRegistry(tools, null, loadTool);
        var store = new InMemoryConversationStore();
        return AgentHarness.Create(llm, registry, store, session: session);
    }

    private static AgentToolCall Call(string id, string name, string argumentsJson = "{}") => new()
    {
        Id = id,
        Name = name,
        ArgumentsJson = argumentsJson
    };

    private static string ToolMessage(AgentChatRequest request)
    {
        var message = request.Messages.Last(candidate => candidate.Role == AgentRole.Tool);
        return message.Content ?? string.Empty;
    }

    [Fact]
    public async Task FirstWriteResult_CarriesThePackSkillOnce()
    {
        var session = new AgentSessionContext();
        var llm = new FakeAgentLlmClient();
        var orchestrator = CreateHarness(
            session,
            llm,
            new ScriptedTool("rename_views", AgentToolRisk.Write),
            new ScriptedTool("list_families", AgentToolRisk.Read));

        llm.EnqueueAssistant(null, new[] { Call("call_1", "rename_views") });
        llm.EnqueueAssistant("готово");

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(), "переименуй вид", Array.Empty<AgentAttachment>(), null,
            CancellationToken.None);

        var toolJson = ToolMessage(llm.Requests[1]);
        using var document = JsonDocument.Parse(toolJson);
        var skills = document.RootElement.GetProperty("skills").EnumerateArray().ToList();
        Assert.Single(skills);
        Assert.Equal("views", skills[0].GetProperty("pack").GetString());
        // Sanity: the payload is the real views manual, not a stub.
        var markdown = skills[0].GetProperty("markdown").GetString() ?? string.Empty;
        Assert.True(markdown.Length > 2000, "markdown length=" + markdown.Length);
        Assert.Contains("## Пак views", markdown, StringComparison.Ordinal);
        Assert.Contains("use_delete_sheets", markdown, StringComparison.Ordinal);
        Assert.Contains("views", session.SentPackSkills);
    }

    [Fact]
    public async Task SecondWriteResult_DoesNotRepeatTheSkill()
    {
        var session = new AgentSessionContext();
        var llm = new FakeAgentLlmClient();
        var orchestrator = CreateHarness(
            session,
            llm,
            new ScriptedTool("rename_views", AgentToolRisk.Write));

        llm.EnqueueAssistant(null, new[] { Call("call_1", "rename_views") });
        llm.EnqueueAssistant("готово");

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(), "переименуй вид", Array.Empty<AgentAttachment>(), null,
            CancellationToken.None);

        Assert.Contains("\"skills\"", ToolMessage(llm.Requests[1]), StringComparison.Ordinal);
        Assert.Single(session.SentPackSkills);
    }

    [Fact]
    public async Task LoadToolPackSkill_IsNotCappedByTheSanitizer()
    {
        // The result sanitizer caps untrusted strings at 400 characters; a pack manual
        // must survive it whole, otherwise load_tool_pack delivered only its first
        // paragraph (#400).
        var session = new AgentSessionContext();
        var llm = new FakeAgentLlmClient();
        var loadTool = new LoadToolPackTool(session);
        var registry = new AgentToolRegistry(
            new IAgentTool[] { new ScriptedTool("list_filters"), loadTool }, null, loadTool);
        var orchestrator = AgentHarness.Create(
            llm, registry, new InMemoryConversationStore(), session: session);

        llm.EnqueueAssistant(
            null,
            new[] { Call("call_1", "load_tool_pack", "{\"pack\":\"filters\"}") });
        llm.EnqueueAssistant("готово");

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(), "настрой фильтр", Array.Empty<AgentAttachment>(), null,
            CancellationToken.None);

        using var document = JsonDocument.Parse(ToolMessage(llm.Requests[1]));
        // load_tool_pack carries its skills inside data; the write-result channel uses
        // the root 'skills' property (ToolResult.Skills).
        Assert.True(
            document.RootElement.TryGetProperty("data", out var data),
            ToolMessage(llm.Requests[1]));
        Assert.True(
            data.TryGetProperty("skills", out var skillsPayload),
            ToolMessage(llm.Requests[1]));
        var markdown = skillsPayload[0]
            .GetProperty("markdown").GetString() ?? string.Empty;

        Assert.True(markdown.Length > 1000, "markdown length=" + markdown.Length);
        Assert.Contains("SetIsFilterEnabled", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadToolResult_NeverCarriesASkill()
    {
        var session = new AgentSessionContext();
        var llm = new FakeAgentLlmClient();
        var orchestrator = CreateHarness(
            session,
            llm,
            new ScriptedTool("list_families", AgentToolRisk.Read));

        llm.EnqueueAssistant(null, new[] { Call("call_1", "list_families") });
        llm.EnqueueAssistant("готово");

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(), "покажи семейства", Array.Empty<AgentAttachment>(), null,
            CancellationToken.None);

        Assert.DoesNotContain("skills", ToolMessage(llm.Requests[1]), StringComparison.Ordinal);
        Assert.Empty(session.SentPackSkills);
    }
}