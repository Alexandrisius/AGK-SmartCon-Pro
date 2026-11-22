using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.Services.Llm;

public sealed partial class OpenAiCompatibleLlmClient
{
    /// <summary>
    /// A successful HTTP response together with its per-attempt cancellation
    /// plumbing. The read loop re-arms the idle deadline
    /// (<see cref="ArmIdle"/>) after every chunk; the absolute cap keeps its own
    /// linked source so a slow trickle can never outlive <c>MaxRequestSeconds</c>.
    /// </summary>
    private sealed class TimedResponse : IDisposable
    {
        private readonly CancellationTokenSource _attemptCts;
        private readonly CancellationTokenSource _capCts;
        private readonly TimeSpan _idle;

        public TimedResponse(TimeSpan idle, TimeSpan cap, CancellationToken ct)
        {
            _idle = idle;
            _capCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _capCts.CancelAfter(cap);
            _attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _capCts.Token);
            _attemptCts.CancelAfter(idle);
        }

        public HttpResponseMessage? HttpResponse { get; private set; }

        public bool Accepted { get; private set; }

        public CancellationToken ReadToken => _attemptCts.Token;

        /// <summary>Re-arms the idle deadline; the cap deadline stays linked.</summary>
        public void ArmIdle()
        {
            try
            {
                _attemptCts.CancelAfter(_idle);
            }
            catch (ObjectDisposedException)
            {
                // The token fired between two chunks; the pending read observes it.
            }
        }

        public void Accept(HttpResponseMessage response)
        {
            HttpResponse = response;
            Accepted = true;
        }

        public void Dispose()
        {
            _attemptCts.Dispose();
            _capCts.Dispose();
            HttpResponse?.Dispose();
        }
    }

    private string BuildPayloadJson(AgentChatRequest request, AgentCachePlan cachePlan)
    {
        var reasoning = _capabilities.ReasoningOptions();
        var (budget, omit) = _capabilities.EffectiveBudget(request.MaxOutputTokens);
        return JsonSerializer.Serialize(
            LlmPayloadBuilder.Build(request, cachePlan, reasoning, budget, omit));
    }

    private string BuildEndpoint() =>
        _settings.BaseUrl.TrimEnd('/') + "/chat/completions";

    private async Task<TimedResponse> SendWithRetryAsync(
        AgentChatRequest request,
        AgentCachePlan cachePlan,
        CancellationToken ct)
    {
        Exception? lastError = null;
        var endpoint = BuildEndpoint();
        var http = _http;
        var attempt = 0;
        var degradations = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var payloadJson = BuildPayloadJson(request, cachePlan);
            var timed = new TimedResponse(_idleTimeout, _capTimeout, ct);
            HttpResponseMessage? response = null;
            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
                httpRequest.Headers.TryAddWithoutValidation("X-Title", "SmartCon");
                httpRequest.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
                if (_settings.IsOpenCodeGo)
                {
                    httpRequest.Headers.TryAddWithoutValidation(
                        "x-opencode-session",
                        cachePlan.OpenCodeSession ?? _openCodeSession);
                }
                httpRequest.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                response = await http
                    .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timed.ReadToken)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    timed.Accept(response);
                    return timed;
                }

                var statusCode = response.StatusCode;
                var status = (int)statusCode;
                var body = await SafeReadBodyAsync(response).ConfigureAwait(false);
                var retryAfter = GetRetryAfter(response);

                if (status == 400 && degradations < MaxDegradations)
                {
                    var change = _capabilities.TryDegrade(body, request.MaxOutputTokens);
                    if (change is not null)
                    {
                        degradations++;
                        SmartConLogger.Warn(
                            "Agent provider rejected a request parameter: " + change +
                            " [Action: the value is lowered for this provider until Revit restarts; " +
                            "pick the reasoning format explicitly in the agent settings if this is wrong]");
                        continue;
                    }
                }

                var message = "LLM request failed: HTTP " + status + ": " + Truncate(body, 800) +
                    BuildFailureHint(status, body);
                if (!IsTransient(statusCode) || attempt == MaxAttempts - 1)
                    throw new AgentLlmException(message, status);

                lastError = new AgentLlmException(message, status);
                attempt++;
                await DelayBeforeRetryAsync(attempt, retryAfter, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                if (attempt == MaxAttempts - 1)
                {
                    throw new AgentLlmException(
                        "LLM transport error: " + ex.Message +
                        ". Проверьте, что Revit разрешён в брандмауэре; при необходимости " +
                        "настройте системный прокси (переменная HTTP_PROXY).",
                        null,
                        ex);
                }

                lastError = ex;
                attempt++;
                await DelayBeforeRetryAsync(attempt, null, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // Idle timeout or the absolute cap: retry like any timeout.
                if (attempt == MaxAttempts - 1)
                    throw new AgentLlmException("LLM request timed out.", null);

                lastError = new TimeoutException("LLM request timed out.");
                attempt++;
                await DelayBeforeRetryAsync(attempt, null, ct).ConfigureAwait(false);
            }
            finally
            {
                if (!timed.Accepted)
                {
                    response?.Dispose();
                    timed.Dispose();
                }
            }
        }
    }

    private string BuildFailureHint(int status, string body)
    {
        if (status == 400 && body.IndexOf("MissingSessionID", StringComparison.Ordinal) >= 0)
        {
            return " [Action: OpenCode Go требует заголовок x-opencode-session — " +
                "перезапустите Revit, чтобы клиент пересоздал сессию]";
        }

        if (status == 401 || status == 403)
            return " [Action: проверьте API-ключ провайдера " + _settings.Provider + "]";

        return string.Empty;
    }

    private static bool IsTransient(HttpStatusCode statusCode)
    {
        var status = (int)statusCode;
        return status == 408 || status == 409 || status == 429 || status >= 500;
    }

    /// <summary>Backoff before the given (1-based) upcoming attempt.</summary>
    private static async Task DelayBeforeRetryAsync(
        int upcomingAttempt,
        TimeSpan? retryAfter,
        CancellationToken ct)
    {
        var delay = retryAfter ?? TimeSpan.FromSeconds(1 << Math.Max(0, upcomingAttempt - 1));
        if (delay > TimeSpan.FromSeconds(30))
            delay = TimeSpan.FromSeconds(30);

        SmartConLogger.Warn(
            "LLM retry " + upcomingAttempt + "/" + MaxAttempts +
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
