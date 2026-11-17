using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Llm;

public sealed class OpenAiCompatibleLlmClient : IAgentLlmClient, IDisposable
{
    private const int MaxAttempts = 3;

    private readonly AgentLlmSettings _settings;
    private readonly bool _hasCustomHandler;
    private readonly string? _openCodeSession;
    private HttpClient _http;
    private string _proxyUrlUsed;

    public OpenAiCompatibleLlmClient(AgentLlmSettings settings, HttpMessageHandler? handler = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _hasCustomHandler = handler is not null;
        _proxyUrlUsed = settings.ProxyUrl ?? string.Empty;
        _http = handler is null ? CreateHttpClient(_proxyUrlUsed) : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(10, settings.TimeoutSeconds));
        _openCodeSession = settings.IsOpenCodeGo ? Guid.NewGuid().ToString("N") : null;
    }

    public string ProviderName => _settings.Provider;

    public string Model => _settings.Model;

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        AgentChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!_settings.HasApiKey)
        {
            throw new AgentLlmException(
                "LLM API key is not configured. Set SMARTCON_OPENROUTER_API_KEY " +
                "as an environment variable or in %APPDATA%\\AGK\\SmartCon\\Agent\\secrets.env.");
        }

        var payloadJson = JsonSerializer.Serialize(LlmPayloadBuilder.Build(request));
        using var _scope = SmartConLogger.BeginScope("Agent",
            ("Method", nameof(StreamAsync)),
            ("Provider", _settings.Provider),
            ("Model", request.Model));

        var response = await SendWithRetryAsync(payloadJson, ct).ConfigureAwait(false);
        using (response)
        {
            var accumulator = new LlmResponseAccumulator();
#if NET8_0_OR_GREATER
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
#else
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var buffer = new List<LlmStreamEvent>();
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                #if NET8_0_OR_GREATER
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
#else
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
#endif
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
                ", toolCalls=" + completion.Message.ToolCalls.Count);

            if (completion.Usage != AgentUsage.Empty)
                yield return LlmStreamEvent.UsageReceived(completion.Usage);

            yield return LlmStreamEvent.Completed(completion);
        }
    }

    public void Dispose() => _http?.Dispose();

    private HttpClient EnsureHttpClient()
    {
        var desiredProxy = _settings.ProxyUrl ?? string.Empty;
        if (_hasCustomHandler
            || string.Equals(desiredProxy, _proxyUrlUsed, StringComparison.Ordinal))
        {
            return _http;
        }

        _http.Dispose();
        _http = CreateHttpClient(desiredProxy);
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(10, _settings.TimeoutSeconds));
        _proxyUrlUsed = desiredProxy;
        return _http;
    }

    private static HttpClient CreateHttpClient(string? proxyUrl)
    {
        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            handler.Proxy = new WebProxy(proxyUrl);
            handler.UseProxy = true;
        }

        return new HttpClient(handler);
    }

    private string BuildEndpoint() =>
        _settings.BaseUrl.TrimEnd('/') + "/chat/completions";

    private async Task<HttpResponseMessage> SendWithRetryAsync(string payloadJson, CancellationToken ct)
    {
        Exception? lastError = null;
        var endpoint = BuildEndpoint();
        var http = EnsureHttpClient();

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
            httpRequest.Headers.TryAddWithoutValidation("X-Title", "SmartCon");
            if (_openCodeSession is not null)
                httpRequest.Headers.TryAddWithoutValidation("x-opencode-session", _openCodeSession);
            httpRequest.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

            try
            {
                var response = await http
                    .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                    return response;

                var statusCode = response.StatusCode;
                var status = (int)statusCode;
                var body = await SafeReadBodyAsync(response).ConfigureAwait(false);
                var retryAfter = GetRetryAfter(response);
                response.Dispose();

                var message = "LLM request failed: HTTP " + status + ": " + Truncate(body, 800);
                if (!IsTransient(statusCode) || attempt == MaxAttempts - 1)
                    throw new AgentLlmException(message, status);

                lastError = new AgentLlmException(message, status);
                await DelayBeforeRetryAsync(attempt, retryAfter, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                if (attempt == MaxAttempts - 1)
                {
                    throw new AgentLlmException(
                        "LLM transport error: " + ex.Message +
                        ". Проверьте, что Revit разрешён в брандмауэре, или укажите прокси " +
                        "в настройках агента (шестерёнка → Прокси).",
                        null,
                        ex);
                }

                lastError = ex;
                await DelayBeforeRetryAsync(attempt, null, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt == MaxAttempts - 1)
                    throw new AgentLlmException("LLM request timed out.", null);

                lastError = new TimeoutException("LLM request timed out.");
                await DelayBeforeRetryAsync(attempt, null, ct).ConfigureAwait(false);
            }
        }

        throw new AgentLlmException(
            "LLM request failed after retries: " + (lastError?.Message ?? "unknown error"),
            null,
            lastError);
    }

    private static bool IsTransient(HttpStatusCode statusCode)
    {
        var status = (int)statusCode;
        return status == 408 || status == 409 || status == 429 || status >= 500;
    }

    private static async Task DelayBeforeRetryAsync(
        int attempt,
        TimeSpan? retryAfter,
        CancellationToken ct)
    {
        var delay = retryAfter ?? TimeSpan.FromSeconds(1 << attempt);
        if (delay > TimeSpan.FromSeconds(30))
            delay = TimeSpan.FromSeconds(30);

        SmartConLogger.Warn(
            "LLM retry " + (attempt + 1) + "/" + MaxAttempts +
            " in " + delay.TotalSeconds.ToString("F0") + "s " +
            "[Action: check provider status and API limits]");
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return delta;
        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                return wait;
        }

        return null;
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value.Remove(maxLength) + "…";
}