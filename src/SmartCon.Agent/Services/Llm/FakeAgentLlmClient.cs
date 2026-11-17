using System.Runtime.CompilerServices;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Llm;

public sealed class FakeAgentLlmClient : IAgentLlmClient
{
    private readonly Queue<AgentChatResponse> _responses = new();
    private readonly List<AgentChatRequest> _requests = new();

    public FakeAgentLlmClient(string model = "fake-model")
    {
        Model = model;
    }

    public string ProviderName => "fake";

    public string Model { get; }

    public IReadOnlyList<AgentChatRequest> Requests => _requests;

    public FakeAgentLlmClient Enqueue(AgentChatResponse response)
    {
        _responses.Enqueue(response);
        return this;
    }

    public FakeAgentLlmClient EnqueueAssistant(
        string? content,
        IReadOnlyList<AgentToolCall>? toolCalls = null,
        string? reasoningContent = null,
        AgentUsage? usage = null)
    {
        return Enqueue(new AgentChatResponse
        {
            Message = AgentMessage.Assistant(content, toolCalls, reasoningContent),
            Usage = usage ?? AgentUsage.Empty,
            Model = Model,
            Provider = ProviderName
        });
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        AgentChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();

        _requests.Add(request);
        if (_responses.Count == 0)
            throw new InvalidOperationException("FakeAgentLlmClient has no queued response.");

        var response = _responses.Dequeue();
        if (!string.IsNullOrEmpty(response.Message.Content))
            yield return LlmStreamEvent.TextDelta(response.Message.Content!);

        if (response.Usage != AgentUsage.Empty)
            yield return LlmStreamEvent.UsageReceived(response.Usage);

        yield return LlmStreamEvent.Completed(response);
    }
}