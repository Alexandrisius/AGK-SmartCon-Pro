using Xunit;
using SmartCon.Agent.Services;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentHistoryCompactorTests
{
    [Fact]
    public void Compact_BelowThreshold_KeepsEverything()
    {
        var messages = BuildHistory(3, payloadLength: 100);

        var (result, compacted) = AgentHistoryCompactor.Compact(
            messages,
            contextBudgetTokens: 200_000);

        Assert.Equal(0, compacted);
        Assert.Equal(messages.Count, result.Count);
    }

    [Fact]
    public void Compact_AboveThreshold_ReplacesOldToolResults()
    {
        var messages = BuildHistory(10, payloadLength: 40_000);

        var (result, compacted) = AgentHistoryCompactor.Compact(
            messages,
            contextBudgetTokens: 1000,
            threshold: 0.6);

        Assert.Equal(6, compacted);
        Assert.Equal(messages.Count, result.Count);

        var toolMessages = result.Where(message => message.Role == AgentRole.Tool).ToList();
        Assert.Equal(10, toolMessages.Count);

        Assert.Contains("\"compacted\":true", toolMessages[0].Content);
        Assert.Contains("compacted", toolMessages[5].Content);
        Assert.DoesNotContain("\"compacted\":true", toolMessages[9].Content);
    }

    [Fact]
    public void Compact_PreservesToolCallIds()
    {
        var messages = BuildHistory(8, payloadLength: 40_000);

        var (result, _) = AgentHistoryCompactor.Compact(
            messages,
            contextBudgetTokens: 1000);

        for (var i = 0; i < messages.Count; i++)
        {
            Assert.Equal(messages[i].Role, result[i].Role);
            Assert.Equal(messages[i].ToolCallId, result[i].ToolCallId);
        }
    }

    [Fact]
    public void Compact_KeepsSuccessFlagInDigest()
    {
        var messages = new List<AgentMessage>
        {
            AgentMessage.Assistant(
                null,
                new[] { new AgentToolCall { Id = "c1", Name = "query_elements", ArgumentsJson = "{}" } }),
            AgentMessage.ToolResult(
                "c1",
                "{\"success\":false,\"errorCode\":\"boom\",\"message\":\"Tool exploded\",\"affectedCount\":0,\"pad\":\"" + new string('p', 400) + "\"}"),
            AgentMessage.Assistant(
                null,
                new[] { new AgentToolCall { Id = "c2", Name = "query_elements", ArgumentsJson = "{}" } }),
            AgentMessage.ToolResult(
                "c2",
                "{\"success\":true,\"message\":\"2 elements\",\"affectedCount\":2,\"pad\":\"" + new string('p', 400) + "\"}"),
            AgentMessage.Assistant(
                null,
                new[] { new AgentToolCall { Id = "c3", Name = "query_elements", ArgumentsJson = "{}" } }),
            AgentMessage.ToolResult(
                "c3",
                "{\"success\":true,\"message\":\"3 elements\",\"affectedCount\":3,\"pad\":\"" + new string('p', 400) + "\"}"),
            AgentMessage.Assistant(
                null,
                new[] { new AgentToolCall { Id = "c4", Name = "query_elements", ArgumentsJson = "{}" } }),
            AgentMessage.ToolResult(
                "c4",
                "{\"success\":true,\"message\":\"4 elements\",\"affectedCount\":4,\"pad\":\"" + new string('p', 400) + "\"}"),
            AgentMessage.Assistant(
                null,
                new[] { new AgentToolCall { Id = "c5", Name = "query_elements", ArgumentsJson = "{}" } }),
            AgentMessage.ToolResult(
                "c5",
                "{\"success\":true,\"message\":\"5 elements\",\"affectedCount\":5,\"pad\":\"" + new string('p', 400) + "\"}")
        };

        var (result, compacted) = AgentHistoryCompactor.Compact(
            messages,
            contextBudgetTokens: 1000,
            keepRecent: 1);

        Assert.True(compacted > 0);
        var firstDigest = result.First(message => message.Role == AgentRole.Tool);
        Assert.Contains("\"success\":false", firstDigest.Content);
        Assert.Contains("Tool exploded", firstDigest.Content);
    }

    [Fact]
    public void Compact_TooFewToolMessages_DoesNothing()
    {
        var messages = new List<AgentMessage>
        {
            AgentMessage.User(new string('x', 100_000), Array.Empty<AgentAttachment>()),
            AgentMessage.Assistant(new string('y', 100_000), Array.Empty<AgentToolCall>())
        };

        var (result, compacted) = AgentHistoryCompactor.Compact(
            messages,
            contextBudgetTokens: 1000);

        Assert.Equal(0, compacted);
        Assert.Equal(2, result.Count);
    }

    private static List<AgentMessage> BuildHistory(int toolCalls, int payloadLength)
    {
        var payload = new string('x', payloadLength);
        var messages = new List<AgentMessage>();
        for (var i = 0; i < toolCalls; i++)
        {
            var id = "call_" + i;
            messages.Add(AgentMessage.Assistant(
                null,
                new[] { new AgentToolCall { Id = id, Name = "query_elements", ArgumentsJson = "{}" } }));
            messages.Add(AgentMessage.ToolResult(
                id,
                "{\"success\":true,\"message\":\"ok\",\"affectedCount\":1,\"pad\":\"" + payload + "\"}"));
        }

        return messages;
    }
}
