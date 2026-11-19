using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.Services.Decisions;

/// <summary>
/// HTTP client for System One decision models (TypeSafe /v1/systemone,
/// OpenRouter /api/alpha/decisions, Perplexity /v1/decisions — same request and
/// response shape). Fail-closed: any transport or protocol error throws, and
/// callers treat it as "unavailable".
/// </summary>
public sealed class HttpDecisionClient : IAgentDecisionClient, IDisposable
{
    private readonly AgentDecisionsSettings _settings;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly DecisionCostTracker? _costTracker;

    public HttpDecisionClient(
        AgentDecisionsSettings settings,
        HttpClient? http = null,
        DecisionCostTracker? costTracker = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _http = http ?? new HttpClient();
        _ownsHttpClient = http is null;
        _costTracker = costTracker;
        _http.Timeout = TimeSpan.FromMilliseconds(settings.TimeoutMilliseconds);
    }

    public string ProviderName => _settings.Provider;

    public string Model => _settings.Model;

    public bool IsEnabled => _settings.IsAvailable;

    public async Task<IReadOnlyDictionary<string, AgentDecisionAnswer>> AskAsync(
        string state,
        IReadOnlyDictionary<string, AgentDecisionQuestion> questions,
        CancellationToken ct)
    {
        if (!IsEnabled)
            return new Dictionary<string, AgentDecisionAnswer>(StringComparer.Ordinal);

        if (questions.Count == 0)
            return new Dictionary<string, AgentDecisionAnswer>(StringComparer.Ordinal);

        var questionPayload = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in questions)
        {
            var question = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = pair.Value.Type,
                ["instructions"] = pair.Value.Instructions
            };
            if (pair.Value.Criteria is { Count: > 0 })
                question["criteria"] = pair.Value.Criteria;

            questionPayload[pair.Key] = question;
        }

        var body = JsonSerializer.Serialize(new
        {
            model = _settings.Model,
            state,
            questions = questionPayload
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _settings.ApiKey);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
#if NET8_0_OR_GREATER
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
#else
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#endif
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                "Decision model HTTP " + (int)response.StatusCode + ": " +
                (text.Length <= 200 ? text : text.Substring(0, 200)));
        }

        return Parse(text, out var costUsd, _costTracker);
    }

    internal static IReadOnlyDictionary<string, AgentDecisionAnswer> Parse(string json)
    {
        return Parse(json, out _, null);
    }

    internal static IReadOnlyDictionary<string, AgentDecisionAnswer> Parse(
        string json,
        out decimal costUsd,
        DecisionCostTracker? costTracker = null)
    {
        costUsd = 0m;
        var answers = new Dictionary<string, AgentDecisionAnswer>(StringComparer.Ordinal);

        using var document = JsonDocument.Parse(json);

        if (document.RootElement.TryGetProperty("usage", out var usageElement)
            && usageElement.ValueKind == JsonValueKind.Object)
        {
            costUsd = ReadCost(usageElement);
            if (costUsd > 0m)
                costTracker?.Add(costUsd);
        }

        if (!document.RootElement.TryGetProperty("answers", out var answersElement)
            || answersElement.ValueKind != JsonValueKind.Object)
        {
            return answers;
        }

        foreach (var property in answersElement.EnumerateObject())
        {
            var value = property.Value;
            var type = value.TryGetProperty("type", out var typeElement)
                       && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()!
                : string.Empty;

            if (type == "refusal")
            {
                answers[property.Name] = new AgentDecisionAnswer
                {
                    Type = "refusal",
                    Error = "refusal"
                };
                continue;
            }

            double? noul = null;
            if (value.TryGetProperty("noul", out var noulElement)
                && noulElement.ValueKind == JsonValueKind.Number)
            {
                noul = noulElement.GetDouble();
            }

            string? choice = null;
            if (value.TryGetProperty("choice", out var choiceElement)
                && choiceElement.ValueKind == JsonValueKind.String)
            {
                choice = choiceElement.GetString();
            }

            Dictionary<string, double>? probabilities = null;
            if (value.TryGetProperty("probabilities", out var probabilitiesElement)
                && probabilitiesElement.ValueKind == JsonValueKind.Object)
            {
                probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var entry in probabilitiesElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind == JsonValueKind.Number)
                        probabilities[entry.Name] = entry.Value.GetDouble();
                }
            }

            double? confidence = null;
            if (value.TryGetProperty("confidence", out var confidenceElement)
                && confidenceElement.ValueKind == JsonValueKind.Number)
            {
                confidence = confidenceElement.GetDouble();
            }

            answers[property.Name] = new AgentDecisionAnswer
            {
                Type = type,
                Noul = noul,
                Choice = choice,
                Probabilities = probabilities,
                Confidence = confidence
            };
        }

        return answers;
    }

    private static decimal ReadCost(JsonElement usage)
    {
        if (!usage.TryGetProperty("cost", out var costElement))
            return 0m;

        if (costElement.ValueKind == JsonValueKind.Number)
            return (decimal)costElement.GetDouble();

        if (costElement.ValueKind == JsonValueKind.String
            && decimal.TryParse(costElement.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 0m;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
}
