using System.Security.Cryptography;
using System.Text;

namespace SmartCon.Core.Services.Agent;

public enum LoopGuardAction
{
    Allow = 0,
    Block = 1,
    Abort = 2
}

/// <summary>
/// Verdict for one tool call produced by <see cref="LoopGuard.BeforeCall"/> or
/// <see cref="LoopGuard.ObserveResult"/>.
/// </summary>
public sealed class LoopGuardDecision
{
    public LoopGuardAction Action { get; init; }

    public int IdenticalCalls { get; init; }

    public int IdenticalErrors { get; init; }

    public bool WarnOnly { get; init; }

    public string? Hint { get; init; }
}

/// <summary>
/// Stops the agent from hammering the same tool call (runbook H1-10). Calls are
/// identified by SHA-256(tool name + "\n" + trimmed arguments); different
/// arguments keep independent counters. The third identical call is blocked, the
/// fifth aborts the turn. Two failed executions of the same call only add a
/// warning so a successful retry stays possible. The guard is per turn:
/// call <see cref="Reset"/> when a new turn starts.
/// </summary>
public sealed class LoopGuard
{
    public const int BlockThreshold = 3;
    public const int AbortThreshold = 5;
    public const int ErrorWarnThreshold = 2;
    public const int MaxEntries = 200;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _insertionOrder = new();

    public LoopGuardDecision BeforeCall(string toolName, string? argumentsJson)
    {
        lock (_gate)
        {
            var key = Hash(toolName, argumentsJson);
            var entry = GetOrCreate(key);
            entry.Calls++;

            if (entry.Calls >= AbortThreshold)
            {
                return new LoopGuardDecision
                {
                    Action = LoopGuardAction.Abort,
                    IdenticalCalls = entry.Calls,
                    IdenticalErrors = entry.Errors,
                    Hint = "This exact call was repeated " + entry.Calls +
                           " times. Stop repeating it and ask the user how to proceed."
                };
            }

            if (entry.Calls >= BlockThreshold)
            {
                return new LoopGuardDecision
                {
                    Action = LoopGuardAction.Block,
                    IdenticalCalls = entry.Calls,
                    IdenticalErrors = entry.Errors,
                    Hint = "This exact call was already made " + entry.Calls +
                           " times. Do not repeat it; change the arguments or ask the user " +
                           "what to do differently."
                };
            }

            return new LoopGuardDecision
            {
                Action = LoopGuardAction.Allow,
                IdenticalCalls = entry.Calls,
                IdenticalErrors = entry.Errors
            };
        }
    }

    public LoopGuardDecision ObserveResult(
        string toolName,
        string? argumentsJson,
        bool success)
    {
        lock (_gate)
        {
            var key = Hash(toolName, argumentsJson);
            var entry = GetOrCreate(key);

            if (success)
            {
                entry.Errors = 0;
                return new LoopGuardDecision
                {
                    Action = LoopGuardAction.Allow,
                    IdenticalCalls = entry.Calls,
                    IdenticalErrors = 0
                };
            }

            entry.Errors++;
            var warn = entry.Errors == ErrorWarnThreshold;
            return new LoopGuardDecision
            {
                Action = LoopGuardAction.Allow,
                IdenticalCalls = entry.Calls,
                IdenticalErrors = entry.Errors,
                WarnOnly = warn,
                Hint = warn
                    ? "The same call failed twice; change the arguments/approach or ask the user."
                    : null
            };
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _entries.Clear();
            _insertionOrder.Clear();
        }
    }

    internal static string Hash(string toolName, string? argumentsJson)
    {
        var normalized = (argumentsJson ?? string.Empty).Trim();
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes((toolName ?? string.Empty) + "\n" + normalized);
        var hash = sha.ComputeHash(bytes);
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var value in hash)
            builder.Append(value.ToString("x2"));

        return builder.ToString();
    }

    private Entry GetOrCreate(string key)
    {
        if (_entries.TryGetValue(key, out var existing))
            return existing;

        var entry = new Entry();
        _entries[key] = entry;
        _insertionOrder.AddLast(key);

        while (_entries.Count > MaxEntries && _insertionOrder.First is { } oldest)
        {
            var oldestKey = oldest.Value;
            _insertionOrder.RemoveFirst();
            _entries.Remove(oldestKey);
        }

        return entry;
    }

    private sealed class Entry
    {
        public int Calls { get; set; }

        public int Errors { get; set; }
    }
}
