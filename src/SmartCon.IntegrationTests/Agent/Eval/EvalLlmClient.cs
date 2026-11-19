using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Models.Agent.Eval;

namespace SmartCon.IntegrationTests.Agent.Eval;

internal enum EvalMode
{
    Replay,
    Live,
    LiveRecord
}

/// <summary>A stable reference to a Revit element recorded in a fixture.</summary>
internal sealed class EvalIdRef
{
    public string Kind { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Category { get; set; }

    /// <summary>Order among elements with the same kind/name/category (by id).</summary>
    public int Index { get; set; }
}

internal sealed class EvalToolCall
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string ArgumentsJson { get; set; } = "{}";
}

internal sealed class EvalRequestInfo
{
    public string Model { get; set; } = string.Empty;

    public int MessageCount { get; set; }

    public List<string> ToolNames { get; set; } = new();

    public string? LastUserMessage { get; set; }

    public string? SystemPromptHash { get; set; }
}

internal sealed class EvalResponseInfo
{
    public string? Content { get; set; }

    public string? Error { get; set; }

    public List<EvalToolCall> ToolCalls { get; set; } = new();

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }
}

internal sealed class EvalExchange
{
    public EvalRequestInfo Request { get; set; } = new();

    public EvalResponseInfo Response { get; set; } = new();
}

internal sealed class EvalFixture
{
    public string TaskId { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string RecordedAt { get; set; } = string.Empty;

    public Dictionary<string, EvalIdRef> IdMap { get; set; } = new();

    public List<EvalExchange> Exchanges { get; set; } = new();
}

internal sealed class EvalReplayException : Exception
{
    public EvalReplayException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Record/replay LLM client for the eval harness.
/// - SMARTCON_AGENT_EVAL_REPLAY=1: answers come from Agent/Eval/Fixtures/*.json,
///   no key and no network are needed; a request beyond the recorded count is a
///   task failure ("fixture exhausted").
/// - SMARTCON_AGENT_EVAL_RECORD=1: live OpenRouter-compatible calls are recorded
///   to the fixture of each task (secrets.env required).
/// - neither: live calls without recording (requires SMARTCON_AGENT_LIVE=1).
/// </summary>
internal sealed class EvalLlmClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly EvalMode _mode;
    private readonly string _fixturesDirectory;
    private readonly DogfoodConfig? _config;
    private readonly HttpClient? _http;

    private EvalFixture? _current;
    private int _exchangeIndex;

    private EvalLlmClient(
        EvalMode mode,
        string fixturesDirectory,
        DogfoodConfig? config)
    {
        _mode = mode;
        _fixturesDirectory = fixturesDirectory;
        _config = config;
        if (mode != EvalMode.Replay)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config!.ApiKey);
        }
    }

    public bool Replaying => _mode == EvalMode.Replay;

    public bool Recording => _mode == EvalMode.LiveRecord;

    public string Model => _mode == EvalMode.Replay
        ? _current?.Model ?? "(replay)"
        : _config!.Model;

    public string Provider => _mode == EvalMode.Replay
        ? _current?.Provider ?? "replay"
        : _config!.Provider;

    public EvalFixture? Current => _current;

    public static EvalLlmClient? TryCreate()
    {
        var fixtures = EvalPaths.FixturesDirectory();
        if (IsEnabled("SMARTCON_AGENT_EVAL_REPLAY"))
            return new EvalLlmClient(EvalMode.Replay, fixtures, null);

        var record = IsEnabled("SMARTCON_AGENT_EVAL_RECORD");
        var live = IsEnabled("SMARTCON_AGENT_LIVE");

        // Deterministic default: replay recorded fixtures unless live/record is
        // explicitly requested. A leftover secrets.env must NOT silently switch the
        // eval to live network calls — live providers (e.g. opencode-go) require a
        // session header and otherwise fail the whole run 0/N (MissingSessionID).
        if (!record && !live)
            return new EvalLlmClient(EvalMode.Replay, fixtures, null);

        var config = DogfoodConfig.TryLoad(requireLiveFlag: false);
        if (config is null)
            return null;

        return new EvalLlmClient(
            record ? EvalMode.LiveRecord : EvalMode.Live,
            fixtures,
            config);
    }

    public void BeginTask(AgentEvalTask task)
    {
        _exchangeIndex = 0;

        if (_mode == EvalMode.Replay)
        {
            var path = Path.Combine(_fixturesDirectory, task.Id + ".json");
            if (!File.Exists(path))
            {
                throw new EvalReplayException(
                    "No fixture for task '" + task.Id + "' at " + path +
                    ". Record it with SMARTCON_AGENT_EVAL_RECORD=1 and a configured key.");
            }

            _current = JsonSerializer.Deserialize<EvalFixture>(
                File.ReadAllText(path), JsonOptions);
            if (_current is null || _current.Exchanges.Count == 0)
                throw new EvalReplayException("Fixture '" + path + "' is empty.");

            return;
        }

        _current = new EvalFixture
        {
            TaskId = task.Id,
            Prompt = task.Prompt,
            Model = _config!.Model,
            Provider = _config.Provider,
            RecordedAt = DateTime.UtcNow.ToString("O")
        };
    }

    public async Task<EvalResponseInfo> CompleteAsync(
        IReadOnlyList<Dictionary<string, object?>> messages,
        IReadOnlyList<AgentToolDefinition> tools,
        CancellationToken ct)
    {
        if (_current is null)
            throw new InvalidOperationException("BeginTask was not called.");

        if (_mode == EvalMode.Replay)
        {
            if (_exchangeIndex >= _current.Exchanges.Count)
            {
                throw new EvalReplayException(
                    "Fixture for task '" + _current.TaskId + "' is exhausted after " +
                    _current.Exchanges.Count + " exchange(s): the request count differs from " +
                    "the recording. Re-record the fixture.");
            }

            return _current.Exchanges[_exchangeIndex++].Response;
        }

        var response = await DogfoodClient
            .CompleteAsync(_http!, _config!, messages, tools, ct)
            .ConfigureAwait(false);

        var info = new EvalResponseInfo
        {
            Content = response.Content,
            Error = response.Error,
            PromptTokens = response.PromptTokens,
            CompletionTokens = response.CompletionTokens
        };

        foreach (var call in response.ToolCalls)
        {
            info.ToolCalls.Add(new EvalToolCall
            {
                Id = call.Id,
                Name = call.Name,
                ArgumentsJson = call.ArgumentsJson
            });
        }

        if (_mode == EvalMode.LiveRecord)
        {
            _current.Exchanges.Add(new EvalExchange
            {
                Request = DescribeRequest(messages, tools),
                Response = info
            });
        }

        return info;
    }

    public void EndTask(IReadOnlyDictionary<string, EvalIdRef> idMap)
    {
        if (_mode != EvalMode.LiveRecord || _current is null)
            return;

        _current.IdMap = new Dictionary<string, EvalIdRef>(idMap, StringComparer.Ordinal);

        Directory.CreateDirectory(_fixturesDirectory);
        var path = Path.Combine(_fixturesDirectory, _current.TaskId + ".json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(_current, JsonOptions),
            new UTF8Encoding(false));
    }

    public void Dispose()
    {
        _http?.Dispose();
    }

    private static EvalRequestInfo DescribeRequest(
        IReadOnlyList<Dictionary<string, object?>> messages,
        IReadOnlyList<AgentToolDefinition> tools)
    {
        string? system = null;
        string? lastUser = null;
        foreach (var message in messages)
        {
            if (!message.TryGetValue("role", out var role) || role is not string roleText)
                continue;

            if (string.Equals(roleText, "system", StringComparison.Ordinal)
                && message.TryGetValue("content", out var systemContent)
                && systemContent is string systemText)
            {
                system = systemText;
            }

            if (string.Equals(roleText, "user", StringComparison.Ordinal)
                && message.TryGetValue("content", out var userContent)
                && userContent is string userText)
            {
                lastUser = userText;
            }
        }

        return new EvalRequestInfo
        {
            Model = string.Empty,
            MessageCount = messages.Count,
            ToolNames = tools.Select(tool => tool.Name).ToList(),
            LastUserMessage = lastUser is null ? null : Shorten(lastUser, 300),
            SystemPromptHash = system is null ? null : Hash(system)
        };
    }

    private static string Hash(string text)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var value in bytes)
            builder.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text.Substring(0, max) + "…";

    private static bool IsEnabled(string name) =>
        string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal);
}

internal static class EvalPaths
{
    public static string FixturesDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("SMARTCON_AGENT_EVAL_FIXTURES");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Directory.CreateDirectory(configured!);
            return configured!;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var project = Path.Combine(directory.FullName, "src", "SmartCon.IntegrationTests");
            if (Directory.Exists(project))
            {
                var fixtures = Path.Combine(project, "Agent", "Eval", "Fixtures");
                Directory.CreateDirectory(fixtures);
                return fixtures;
            }

            directory = directory.Parent;
        }

        var fallback = Path.Combine(AppContext.BaseDirectory, "Agent", "Eval", "Fixtures");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    public static string CreateReportDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("SMARTCON_AGENT_EVAL_REPORT_DIR");
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AGK", "SmartCon", "Agent", "eval")
            : configured!;
        var directory = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
