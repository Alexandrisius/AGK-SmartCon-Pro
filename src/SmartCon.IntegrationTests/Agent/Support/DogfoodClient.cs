using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SmartCon.Core.Models.Agent;

namespace SmartCon.IntegrationTests.Agent;

internal sealed class DogfoodToolCall
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string ArgumentsJson { get; set; } = "{}";
}

internal sealed class DogfoodResponse
{
    public string? Content { get; set; }

    public string? Error { get; set; }

    public List<DogfoodToolCall> ToolCalls { get; } = new();

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    public int TotalTokens { get; set; }
}

/// <summary>
/// Minimal OpenAI-compatible client for the live dogfood test. SmartCon.Agent is
/// deliberately not referenced (WPF/RevitAPIUI types destabilise the test host),
/// so this test-only client speaks the same chat-completions protocol.
/// </summary>
internal static class DogfoodClient
{
    internal static async Task<DogfoodResponse> CompleteAsync(
        HttpClient http,
        DogfoodConfig config,
        IReadOnlyList<Dictionary<string, object?>> messages,
        IReadOnlyList<AgentToolDefinition> tools,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = config.Model,
            ["messages"] = messages,
            ["temperature"] = 0.2,
            ["max_tokens"] = 1500,
            ["tools"] = tools.Select(definition => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = definition.Name,
                    ["description"] = definition.Description,
                    ["parameters"] = ParseSchema(definition.InputSchemaJson)
                }
            }).ToList()
        };

        var body = JsonSerializer.Serialize(payload);
        var url = config.BaseUrl.TrimEnd('/') + "/chat/completions";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new DogfoodResponse { Error = ex.GetType().Name + ": " + ex.Message };
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new DogfoodResponse
                {
                    Error = "HTTP " + (int)response.StatusCode + ": " +
                            (text.Length > 300 ? text.Substring(0, 300) : text)
                };
            }

            return Parse(text);
        }
    }

    private static JsonElement ParseSchema(string schemaJson)
    {
        using var document = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(schemaJson) ? "{}" : schemaJson);
        return document.RootElement.Clone();
    }

    private static DogfoodResponse Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return new DogfoodResponse { Error = "no choices in the response" };
            }

            var message = choices[0].GetProperty("message");
            var result = new DogfoodResponse();

            if (message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                result.Content = content.GetString();
            }

            if (message.TryGetProperty("tool_calls", out var toolCalls)
                && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in toolCalls.EnumerateArray())
                {
                    var function = call.GetProperty("function");
                    result.ToolCalls.Add(new DogfoodToolCall
                    {
                        Id = call.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                        Name = function.GetProperty("name").GetString() ?? string.Empty,
                        ArgumentsJson = function.TryGetProperty("arguments", out var arguments)
                            ? arguments.GetString() ?? "{}"
                            : "{}"
                    });
                }
            }

            if (document.RootElement.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object)
            {
                result.PromptTokens = ReadInt(usage, "prompt_tokens");
                result.CompletionTokens = ReadInt(usage, "completion_tokens");
                result.TotalTokens = ReadInt(usage, "total_tokens");
            }

            return result;
        }
        catch (JsonException ex)
        {
            return new DogfoodResponse { Error = "invalid JSON: " + ex.Message };
        }
    }

    private static int ReadInt(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed))
        {
            return parsed;
        }

        return 0;
    }
}

internal sealed class DogfoodConfig
{
    private const string DefaultBaseUrl = "https://openrouter.ai/api/v1";
    private const string DefaultModel = "deepseek/deepseek-v4.1-flash-1017";

    private DogfoodConfig(string provider, string model, string baseUrl, string apiKey)
    {
        Provider = provider;
        Model = model;
        BaseUrl = baseUrl;
        ApiKey = apiKey;
    }

    public string Provider { get; }

    public string Model { get; }

    public string BaseUrl { get; }

    public string ApiKey { get; }

    public static DogfoodConfig? TryLoad(bool requireLiveFlag = true)
    {
        if (requireLiveFlag
            && !string.Equals(
                Environment.GetEnvironmentVariable("SMARTCON_AGENT_LIVE"),
                "1",
                StringComparison.Ordinal))
        {
            return null;
        }

        var values = LoadEnvironment();

        var apiKey = First(values,
            "SMARTCON_AGENT_API_KEY",
            "SMARTCON_OPENROUTER_API_KEY",
            "OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        var provider = First(values, "SMARTCON_LLM_PROVIDER") ?? "openrouter";
        var openCodeGo = string.Equals(provider, "opencode-go", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(provider, "opencode", StringComparison.OrdinalIgnoreCase);

        var baseUrl = openCodeGo
            ? First(values, "SMARTCON_OPENCODE_GO_BASE_URL")
              ?? "https://opencode.ai/inference/go/openai/v1"
            : First(values, "SMARTCON_OPENROUTER_BASE_URL",
                    "SMARTCON_AGENT_BASE_URL") ?? DefaultBaseUrl;

        var model = First(values, "SMARTCON_AGENT_MODEL", "SMARTCON_LLM_MODEL") ?? DefaultModel;

        return new DogfoodConfig(provider, model!, baseUrl!, apiKey!);
    }

    private static Dictionary<string, string> LoadEnvironment()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in new[]
                 {
                     "SMARTCON_AGENT_API_KEY", "SMARTCON_OPENROUTER_API_KEY", "OPENROUTER_API_KEY",
                     "SMARTCON_LLM_PROVIDER", "SMARTCON_AGENT_BASE_URL", "SMARTCON_AGENT_MODEL",
                     "SMARTCON_LLM_MODEL", "SMARTCON_OPENROUTER_BASE_URL",
                     "SMARTCON_OPENCODE_GO_BASE_URL"
                 })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                values[name] = value!;
        }

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AGK", "SmartCon", "Agent", "secrets.env");
        if (!File.Exists(path))
            return values;

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                continue;

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = trimmed.Substring(0, separator).Trim();
            var value = trimmed.Substring(separator + 1).Trim().Trim('"');
            if (key.Length > 0 && value.Length > 0 && !values.ContainsKey(key))
                values[key] = value;
        }

        return values;
    }

    private static string? First(Dictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }
}
