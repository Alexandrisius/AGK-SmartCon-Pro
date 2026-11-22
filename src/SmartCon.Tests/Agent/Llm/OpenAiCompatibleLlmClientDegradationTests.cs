using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Xunit;
using SmartCon.Agent.Services.Llm;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Tests.Agent.Llm;

/// <summary>
/// Graceful provider degradation on HTTP 400 and the streaming idle timeout /
/// absolute cap, measured with fake HTTP handlers.
/// </summary>
public sealed class OpenAiCompatibleLlmClientDegradationTests
{
    private static AgentLlmSettings ZaiSettings(int? timeoutSeconds = null) => new()
    {
        Provider = "zai-glm",
        Model = AgentLlmSettings.DefaultZaiModel,
        BaseUrl = AgentLlmSettings.DefaultZaiBaseUrl,
        ApiKey = "sk-test",
        ReasoningEffort = "max",
        MaxOutputTokens = 64_000,
        TimeoutSeconds = timeoutSeconds ?? 120
    };

    private static AgentChatRequest Request(int? maxOutputTokens = 64_000) => new()
    {
        Model = "glm-5.3",
        Messages = new[] { new AgentMessage { Role = AgentRole.User, Content = "ping" } },
        MaxOutputTokens = maxOutputTokens,
        ConversationId = Guid.NewGuid()
    };

    private static async Task<AgentChatResponse> DrainAsync(
        IAgentLlmClient client,
        AgentChatRequest? request = null)
    {
        AgentChatResponse? completion = null;
        await foreach (var streamEvent in client.StreamAsync(
                 request ?? Request(), CancellationToken.None))
        {
            if (streamEvent.Kind == LlmStreamEventKind.Completed)
                completion = streamEvent.Completion;
        }

        Assert.NotNull(completion);
        return completion!;
    }

    [Fact]
    public async Task Http400_RejectingTheEffortValue_StepsDownAndRemembers()
    {
        var handler = new ScriptedHandler();
        handler.QueueBadRequest(
            """{"error":{"code":"400","message":"Invalid parameter: reasoning_effort value \"max\" is not supported"}}""");
        using var client = new OpenAiCompatibleLlmClient(ZaiSettings(), handler);

        await DrainAsync(client);

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("\"reasoning_effort\":\"max\"", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("\"reasoning_effort\":\"xhigh\"", handler.Bodies[1], StringComparison.Ordinal);

        // The step-down is remembered: later requests do not re-trigger the 400.
        handler.QueueSuccess();
        await DrainAsync(client);
        Assert.Equal(3, handler.Bodies.Count);
        Assert.Contains("\"reasoning_effort\":\"xhigh\"", handler.Bodies[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http400_RejectingTheReasoningParameter_DropsTheFormat()
    {
        var handler = new ScriptedHandler();
        handler.QueueBadRequest(
            """{"error":{"message":"unsupported parameter: reasoning_effort"}}""");
        using var client = new OpenAiCompatibleLlmClient(ZaiSettings(), handler);

        await DrainAsync(client);

        Assert.Equal(2, handler.Bodies.Count);
        Assert.DoesNotContain(
            "\"reasoning_effort\"", handler.Bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http400_RejectingMaxTokens_HalvesThenOmits()
    {
        var handler = new ScriptedHandler();
        handler.QueueBadRequest(
            """{"error":{"code":"1210","message":"The max_tokens parameter is illegal.：限制数值范围[1,131072]"}}""");
        using var client = new OpenAiCompatibleLlmClient(ZaiSettings(), handler);

        // 400 x4 in total (the ctor queued the first): 32000, 16000, 8000,
        // then the field is omitted entirely; the 5th request succeeds.
        for (var i = 0; i < 3; i++)
            handler.QueueBadRequest(
                """{"error":{"code":"1210","message":"The max_tokens parameter is illegal.：限制数值范围[1,131072]"}}""");
        handler.QueueSuccess();

        await DrainAsync(client);

        Assert.Equal(5, handler.Bodies.Count);
        Assert.Contains("\"max_tokens\":32000", handler.Bodies[1], StringComparison.Ordinal);
        Assert.Contains("\"max_tokens\":16000", handler.Bodies[2], StringComparison.Ordinal);
        Assert.Contains("\"max_tokens\":8000", handler.Bodies[3], StringComparison.Ordinal);
        Assert.DoesNotContain("\"max_tokens\"", handler.Bodies[4], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http400_UnrelatedBody_ThrowsAsBefore()
    {
        var handler = new ScriptedHandler();
        handler.QueueBadRequest("""{"error":{"message":"model not found"}}""");
        using var client = new OpenAiCompatibleLlmClient(ZaiSettings(), handler);

        var ex = await Assert.ThrowsAsync<AgentLlmException>(
            () => DrainAsync(client));

        Assert.Equal(400, ex.StatusCode);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task FinishReasonLength_MarksTruncatedInsteadOfThrowing()
    {
        var handler = new ScriptedHandler();
        handler.QueueSse(
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"partial thinking\"},\"finish_reason\":\"length\"}]}\n\n" +
            "data: {\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":64},\"choices\":[]}\n\n" +
            "data: [DONE]\n\n");
        using var client = new OpenAiCompatibleLlmClient(ZaiSettings(), handler);

        var completion = await DrainAsync(client);

        Assert.True(completion.Truncated);
        Assert.Equal("length", completion.FinishReason);
        Assert.Equal(64, completion.Usage.CompletionTokens);
    }

    [Fact]
    public async Task Stream_ChunksSlowerThanTheTotalButFasterThanIdle_Succeeds()
    {
        var settings = ZaiSettings(timeoutSeconds: 1);
        var handler = new StreamingHandler(chunkCount: 8, chunkDelayMs: 200);
        using var client = new OpenAiCompatibleLlmClient(settings, handler);

        var completion = await DrainAsync(client);

        // 8 * 200ms = 1.6s total > 1s; no gap exceeds the 1s idle deadline.
        Assert.False(completion.Truncated);
        Assert.Equal("stop", completion.FinishReason);
    }

    [Fact]
    public async Task Stream_StalledHandler_TimesOutAndRetriesTransiently()
    {
        var settings = ZaiSettings(timeoutSeconds: 1);
        var handler = new StreamingHandler(stallBeforeHeadersMs: 3_000);
        using var client = new OpenAiCompatibleLlmClient(settings, handler);

        var ex = await Assert.ThrowsAsync<AgentLlmException>(
            () => DrainAsync(client));

        Assert.Contains("timed out", ex.Message, StringComparison.Ordinal);
        Assert.Equal(3, handler.Calls);
    }

    /// <summary>Handler with a queue of HTTP outcomes that records every body.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<string> Bodies { get; } = new();

        public void QueueBadRequest(string body) =>
            _responses.Enqueue(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });

        public void QueueSuccess() =>
            QueueSse(
                "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: [DONE]\n\n");

        public void QueueSse(string body) =>
            _responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
            });

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));

            if (_responses.Count == 0)
                QueueSuccess();
            return _responses.Dequeue();
        }
    }

    /// <summary>
    /// Streams SSE chunks with a fixed delay between them (or stalls before
    /// the headers). The response content serves a real segment-by-segment
    /// stream: HttpContent from a fake handler is otherwise buffered fully by
    /// ReadAsStreamAsync, which would make the chunk delays invisible.
    /// </summary>
    private sealed class StreamingHandler : HttpMessageHandler
    {
        private readonly int _chunkCount;
        private readonly int _chunkDelayMs;
        private readonly int _stallBeforeHeadersMs;

        public int Calls { get; private set; }

        public StreamingHandler(
            int chunkCount = 4,
            int chunkDelayMs = 100,
            int stallBeforeHeadersMs = 0)
        {
            _chunkCount = chunkCount;
            _chunkDelayMs = chunkDelayMs;
            _stallBeforeHeadersMs = stallBeforeHeadersMs;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (_stallBeforeHeadersMs > 0)
                await Task.Delay(_stallBeforeHeadersMs, cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new DelayedSseContent(_chunkCount, _chunkDelayMs)
            };
        }
    }

    private sealed class DelayedSseContent : HttpContent
    {
        private readonly int _chunkCount;
        private readonly int _delayMs;

        public DelayedSseContent(int chunkCount, int delayMs)
        {
            _chunkCount = chunkCount;
            _delayMs = delayMs;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            System.Net.TransportContext? context)
        {
            // Never used: CreateContentReadStreamAsync serves the live stream.
            return Task.CompletedTask;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new DelayedSseStream(_chunkCount, _delayMs));
    }

    private sealed class DelayedSseStream : Stream
    {
        private readonly List<byte[]> _segments;
        private readonly int _delayMs;
        private int _index;

        public DelayedSseStream(int chunkCount, int delayMs)
        {
            _delayMs = delayMs;
            _segments = new List<byte[]>();
            for (var i = 0; i < chunkCount; i++)
            {
                _segments.Add(Encoding.UTF8.GetBytes(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\n\n"));
            }

            _segments.Add(Encoding.UTF8.GetBytes(
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"));
            _segments.Add(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_index >= _segments.Count)
                return 0;

            // Every segment after the first costs one delay: the gap between
            // two reads is the delay, while the total exceeds it many times.
            if (_index > 0)
                Thread.Sleep(_delayMs);

            var segment = _segments[_index++];
            var size = Math.Min(segment.Length, count);
            Array.Copy(segment, 0, buffer, offset, size);
            return size;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
