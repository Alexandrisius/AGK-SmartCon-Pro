using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Llm;

public sealed partial class OpenAiCompatibleLlmClient : IAgentLlmClient, IDisposable
{
    private const int MaxAttempts = 3;

    /// <summary>Upper bound of graceful degradation steps per request.</summary>
    private const int MaxDegradations = 6;

    private readonly AgentLlmSettings _settings;
    private readonly string _openCodeSession;
    private readonly string _userAgent;
    private readonly HttpClient _http;
    private readonly AgentProviderCapabilities _capabilities;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _capTimeout;
    private string? _cacheNamespace;

    public OpenAiCompatibleLlmClient(AgentLlmSettings settings, HttpMessageHandler? handler = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        // The total HttpClient timeout is disabled: at reasoning effort max a
        // single streamed answer can run for minutes. Timeouts are driven per
        // attempt by a linked CTS: an idle deadline re-armed after every data
        // chunk, plus an absolute per-attempt cap (see SendWithRetryAsync).
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _idleTimeout = TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds));
        _capTimeout = TimeSpan.FromSeconds(Math.Max(60, settings.MaxRequestSeconds));
        _openCodeSession = "ses_" + Guid.NewGuid().ToString("N");
        _userAgent = "SmartCon-Agent/" +
            (typeof(OpenAiCompatibleLlmClient).Assembly.GetName().Version?.ToString(3) ?? "1.0");
        _capabilities = new AgentProviderCapabilities(
            AgentLlmSettings.NormalizeReasoningEffort(settings.ReasoningEffort),
            settings.EffectiveReasoningFormat);
    }

    public string ProviderName => _settings.Provider;

    public string Model => _settings.Model;

    /// <summary>Effective reasoning request fields after any provider rejection.</summary>
    public string EffectiveReasoningDescription
    {
        get
        {
            var (effort, format) = _capabilities.Snapshot();
            return format switch
            {
                AgentReasoningFormat.ReasoningEffort => "reasoning_effort=" + effort,
                AgentReasoningFormat.ReasoningObject => "reasoning.effort=" + effort,
                _ => "reasoning=none"
            };
        }
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        AgentChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!_settings.HasApiKey)
        {
            var keyName = _settings.IsOpenCodeGo
                ? "SMARTCON_OPENCODE_GO_API_KEY"
                : AgentLlmSettings.IsZaiProvider(_settings.Provider)
                    ? "SMARTCON_ZAI_API_KEY"
                    : "SMARTCON_OPENROUTER_API_KEY";
            throw new AgentLlmException(
                "LLM API key is not configured for provider '" + _settings.Provider +
                "'. Open the agent settings (gear) and enter the key, or set " + keyName +
                " in %APPDATA%\\AGK\\SmartCon\\Agent\\secrets.env.");
        }

        var (effort, format) = _capabilities.Snapshot();
        var cachePlan = AgentProviderCachePolicy.Resolve(
            _settings.Provider,
            request.Model,
            request.Temperature,
            request.ConversationId,
            request.CacheKey,
            effort,
            format);
        LogCachePlan(cachePlan);

        using var _scope = SmartConLogger.BeginScope("Agent",
            ("Method", nameof(StreamAsync)),
            ("Provider", _settings.Provider),
            ("Model", request.Model));

        var timed = await SendWithRetryAsync(request, cachePlan, ct).ConfigureAwait(false);
        using (timed)
        {
            var response = timed.HttpResponse!;
            var readToken = timed.ReadToken;
            var accumulator = new LlmResponseAccumulator();
#if NET8_0_OR_GREATER
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
#else
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // Net48 has no ReadLineAsync(ct): Stop would not interrupt a stream
            // that is waiting for the next line. Disposing the response from the
            // registration unblocks the pending read; the token fires on user
            // Stop, on the idle timeout and on the absolute cap alike.
            using var cancelRegistration = readToken.CanBeCanceled
                ? readToken.Register(
                    static state =>
                    {
                        try
                        {
                            ((HttpResponseMessage)state!).Dispose();
                        }
                        catch
                        {
                            // Best effort: the read loop turns the failure into cancellation.
                        }
                    },
                    response)
                : default;

            var buffer = new List<LlmStreamEvent>();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                timed.ArmIdle();

                string? line;
                try
                {
#if NET8_0_OR_GREATER
                    line = await reader.ReadLineAsync(readToken).ConfigureAwait(false);
#else
                    line = await reader.ReadLineAsync().ConfigureAwait(false);
#endif
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    throw new AgentLlmException(
                        "LLM stream timed out while waiting for data. " +
                        "[Action: retry; if it repeats, raise the timeout in the agent settings]",
                        null,
                        ex);
                }
                catch (ObjectDisposedException) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
                catch (IOException) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
                catch (IOException ex)
                {
                    throw new AgentLlmException(
                        "LLM stream was interrupted: " + ex.Message +
                        " [Action: retry the turn; the partial answer is kept in the chat]",
                        null,
                        ex);
                }

                if (line is null)
                    break;

                if (line.Length == 0 || line[0] == ':')
                    continue;

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                var data = line.Substring(5).Trim();
                if (data.Length == 0)
                    continue;
                if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                    break;

                buffer.Clear();
                try
                {
                    accumulator.Append(data, buffer);
                }
                catch (JsonException ex)
                {
                    SmartConLogger.Warn(
                        "Skipped malformed LLM stream chunk: " + ex.Message +
                        " [Action: check provider compatibility; the turn may lack a tool result]");
                    continue;
                }

                foreach (var streamEvent in buffer)
                    yield return streamEvent;
            }

            var completion = accumulator.Build(_settings.Provider, request.Model);
            SmartConLogger.Info(
                "LLM turn completed: prompt=" + completion.Usage.PromptTokens +
                ", completion=" + completion.Usage.CompletionTokens +
                ", reasoning=" + completion.Usage.ReasoningTokens +
                ", total=" + completion.Usage.TotalTokens +
                ", toolCalls=" + completion.Message.ToolCalls.Count +
                ", finishReason=" + (completion.FinishReason ?? "(none)") +
                ", truncated=" + (completion.Truncated ? "true" : "false") +
                ", reasoningParams=" + EffectiveReasoningDescription);

            if (completion.Usage != AgentUsage.Empty)
                yield return LlmStreamEvent.UsageReceived(completion.Usage);

            yield return LlmStreamEvent.Completed(completion);
        }
    }

    public void Dispose() => _http?.Dispose();

    private void LogCachePlan(AgentCachePlan plan)
    {
        if (plan.Kind == AgentCacheKind.None)
        {
            SmartConLogger.Warn(
                "Agent cache policy: provider '" + _settings.Provider + "' has no cache policy " +
                "[Action: the request is sent without cache hints; add the provider to " +
                "AgentProviderCachePolicy or accept implicit caching]");
        }

        var namespaceChange = AgentProviderCachePolicy.DescribeNamespaceChange(
            _cacheNamespace, plan.Namespace);
        if (namespaceChange is not null)
            SmartConLogger.Info(namespaceChange);

        _cacheNamespace = plan.Namespace;
        var sessionId = plan.ProviderOptions.TryGetValue("session_id", out var session)
            ? session
            : "(none)";
        SmartConLogger.Info(
            "Agent cache plan: namespace=" + plan.Namespace +
            ", kind=" + plan.KindName +
            ", prompt_cache_key=" + (plan.PromptCacheKey ?? "(none)") +
            ", session_id=" + sessionId +
            ", opencode_session=" + (plan.OpenCodeSession ?? "(none)") +
            ", breakpoints=" + (plan.UseAnthropicBreakpoints ? "2" : "0"));
    }
}
