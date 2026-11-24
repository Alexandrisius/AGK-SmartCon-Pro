using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class ReadToolResultToolTests
{
    private static readonly Guid Conversation = Guid.Parse(
        "11111111-2222-3333-4444-555555555555");

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static AgentToolContext Context() =>
        new(new ImmediateAwaitableEvent(), "doc", Conversation);

    private static ReadToolResultTool CreateTool(string? payload) =>
        new(new FakeBlobStore(payload));

    [Fact]
    public async Task Execute_WithoutHandle_IsRejected()
    {
        var tool = CreateTool("{}");

        var result = await tool.ExecuteAsync(
            Context(), Parse("{\"offset\":0}"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("invalid_arguments", result.ErrorCode);
    }

    [Fact]
    public async Task Execute_UnknownHandle_ReturnsNotFound()
    {
        var tool = CreateTool(null);

        var result = await tool.ExecuteAsync(
            Context(), Parse("{\"handle\":\"call_1\"}"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("result_not_found", result.ErrorCode);
    }

    [Fact]
    public async Task Execute_PagesThroughTheContent()
    {
        var payload = new string('a', 30) + new string('b', 30);
        var tool = CreateTool("{\"success\":true,\"message\":\"" + payload + "\"}");

        var first = await tool.ExecuteAsync(
            Context(),
            Parse("{\"handle\":\"call_1\",\"offset\":0,\"limit\":10}"),
            CancellationToken.None);

        Assert.True(first.Success);
        var data = first.Data!.Value;
        Assert.Equal(10, data.GetProperty("returned").GetInt32());
        Assert.Equal(10, data.GetProperty("nextOffset").GetInt32());
        Assert.True(data.GetProperty("truncated").GetBoolean());

        var second = await tool.ExecuteAsync(
            Context(),
            Parse("{\"handle\":\"call_1\",\"offset\":10,\"limit\":10}"),
            CancellationToken.None);

        Assert.True(second.Success);
        Assert.Equal(10, second.Data!.Value.GetProperty("offset").GetInt32());
    }

    [Fact]
    public async Task Execute_ResolvesAPathInsideTheJson()
    {
        var tool = CreateTool(
            "{\"data\":{\"views\":[{\"name\":\"План 1\"},{\"name\":\"План 2\"}]}}");

        var result = await tool.ExecuteAsync(
            Context(),
            Parse("{\"handle\":\"call_1\",\"path\":\"data.views.1.name\"}"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("План 2", result.Data!.Value.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Execute_UnknownPath_IsRejected()
    {
        var tool = CreateTool("{\"data\":{}}");

        var result = await tool.ExecuteAsync(
            Context(),
            Parse("{\"handle\":\"call_1\",\"path\":\"data.views\"}"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("invalid_arguments", result.ErrorCode);
    }

    [Fact]
    public async Task Execute_LimitAboveTheMaximum_IsCutTo20000NotRejected()
    {
        // A limit above 20000 used to die in the schema and cost the model a
        // round; it is cut here and the answer says so.
        var payload = "{\"success\":true,\"message\":\"" + new string('a', 25000) + "\"}";
        var tool = CreateTool(payload);

        var result = await tool.ExecuteAsync(
            Context(),
            Parse("{\"handle\":\"call_1\",\"offset\":0,\"limit\":30000}"),
            CancellationToken.None);

        Assert.True(result.Success);
        var data = result.Data!.Value;
        Assert.Equal(20000, data.GetProperty("limit").GetInt32());
        Assert.Equal(20000, data.GetProperty("returned").GetInt32());
        Assert.Equal(20000, data.GetProperty("limitCappedTo").GetInt32());
        Assert.Equal(payload.Length, data.GetProperty("total").GetInt32());

        var within = await tool.ExecuteAsync(
            Context(),
            Parse("{\"handle\":\"call_1\",\"offset\":0,\"limit\":20000}"),
            CancellationToken.None);

        Assert.True(within.Success);
        Assert.Equal(20000, within.Data!.Value.GetProperty("limit").GetInt32());
        // No cap marker for a limit inside the maximum (null or absent).
        var hasCapped = within.Data.Value.TryGetProperty("limitCappedTo", out var capped)
                        && capped.ValueKind != JsonValueKind.Null;
        Assert.False(hasCapped);
    }

    [Fact]
    public async Task Execute_ResolvesBracketArrayIndex()
    {
        var tool = CreateTool(
            "{\"data\":{\"groups\":[{\"signatureDigest\":\"g1\"},{\"signatureDigest\":\"g2\"}]}}");

        var result = await tool.ExecuteAsync(
            Context(),
            Parse("{\"handle\":\"call_1\",\"path\":\"data.groups[0].signatureDigest\"}"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("g1", result.Data!.Value.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Execute_ResolvesNegativeArrayIndex()
    {
        var tool = CreateTool(
            "{\"data\":{\"groups\":[{\"signatureDigest\":\"g1\"},{\"signatureDigest\":\"g2\"}]}}");

        var result = await tool.ExecuteAsync(
            Context(),
            Parse("{\"handle\":\"call_1\",\"path\":\"data.groups[-1].signatureDigest\"}"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("g2", result.Data!.Value.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Execute_ProjectionReturnsSelectedFieldsAndMapsArrays()
    {
        var tool = CreateTool(
            "{\"data\":{\"groups\":[{\"signatureDigest\":\"g1\",\"count\":2," +
            "\"parameters\":[{\"name\":\"ADSK_Марка\"},{\"name\":\"ADSK_Код\"}]}]}}");

        var result = await tool.ExecuteAsync(
            Context(),
            Parse(
                "{\"handle\":\"call_1\",\"path\":\"data.groups[0]\"," +
                "\"project\":[\"signatureDigest\",\"parameters.name\",\"count\"]}"),
            CancellationToken.None);

        Assert.True(result.Success);
        var data = result.Data!.Value;
        Assert.True(data.GetProperty("projected").GetBoolean());
        using var projected = JsonDocument.Parse(data.GetProperty("content").GetString()!);
        var root = projected.RootElement;
        Assert.Equal("g1", root.GetProperty("signatureDigest").GetString());
        Assert.Equal(2, root.GetProperty("count").GetInt32());
        var names = root.GetProperty("parameters.name");
        Assert.Equal(2, names.GetArrayLength());
        Assert.Equal("ADSK_Марка", names[0].GetString());
        Assert.Equal("ADSK_Код", names[1].GetString());
    }

    [Fact]
    public async Task Execute_ProjectionUnknownPath_IsRejected()
    {
        var tool = CreateTool("{\"data\":{\"groups\":[]}}");

        var result = await tool.ExecuteAsync(
            Context(),
            Parse("{\"handle\":\"call_1\",\"project\":[\"missing.path\"]}"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("invalid_arguments", result.ErrorCode);
    }

    [Fact]
    public async Task Execute_BracketKeyIsAddressableAsALiteralProperty()
    {
        var tool = CreateTool("{\"data\":{\"a[b]\":\"literal-key\"}}");

        var result = await tool.ExecuteAsync(
            Context(),
            Parse("{\"handle\":\"call_1\",\"path\":\"data.a[b]\"}"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("literal-key", result.Data!.Value.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Execute_ProjectionArrayMap_KeepsPositionalNulls()
    {
        var tool = CreateTool(
            "{\"data\":{\"groups\":[{\"parameters\":[{\"name\":\"a\"},{}]}]}}");

        var result = await tool.ExecuteAsync(
            Context(),
            Parse(
                "{\"handle\":\"call_1\",\"path\":\"data.groups[0]\"," +
                "\"project\":[\"parameters.name\"]}"),
            CancellationToken.None);

        Assert.True(result.Success);
        using var projected = JsonDocument.Parse(
            result.Data!.Value.GetProperty("content").GetString()!);
        var names = projected.RootElement.GetProperty("parameters.name");
        Assert.Equal(2, names.GetArrayLength());
        Assert.Equal("a", names[0].GetString());
        Assert.Equal(JsonValueKind.Null, names[1].ValueKind);
    }

    [Fact]
    public async Task Execute_OffsetBeyondEnd_IsRejected()
    {
        var tool = CreateTool("{\"success\":true}");

        var result = await tool.ExecuteAsync(
            Context(),
            Parse("{\"handle\":\"call_1\",\"offset\":1000}"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("offset_out_of_range", result.ErrorCode);
    }

    private sealed class FakeBlobStore : IAgentToolResultBlobStore
    {
        private readonly string? _payload;

        public FakeBlobStore(string? payload)
        {
            _payload = payload;
        }

        public Task SaveAsync(
            Guid sessionId,
            string toolCallId,
            string resultJson,
            CancellationToken ct) => Task.CompletedTask;

        public Task<AgentToolResultBlob?> GetAsync(
            Guid sessionId,
            string toolCallId,
            CancellationToken ct) =>
            Task.FromResult(_payload is null
                ? null
                : new AgentToolResultBlob
                {
                    ToolCallId = toolCallId,
                    SessionId = sessionId,
                    ResultJson = _payload,
                    CreatedAt = DateTime.UtcNow
                });
    }
}
