using System.Diagnostics;
using System.Text;

namespace SmartCon.Core.Logging;

/// <summary>
/// A single active logging scope. Created by
/// <see cref="SmartConLogger.BeginScope(string, ValueTuple{string, object?}[])"/>
/// and pushed onto <see cref="LogScopeProvider"/>. Holds a short
/// <see cref="OpId"/> used as correlation id in every log line written
/// while the scope is active, plus an optional set of structured
/// <see cref="Properties"/> rendered alongside the prefix.
/// </summary>
public class LogScope
{
    internal LogScope(string operation, IReadOnlyList<(string Key, object? Value)> properties, string opId)
        : this(operation, properties, opId, Stopwatch.StartNew())
    {
    }

    internal LogScope(string operation, IReadOnlyList<(string Key, object? Value)> properties, string opId, Stopwatch stopwatch)
    {
        Operation = operation;
        Properties = properties;
        OpId = opId;
        Stopwatch = stopwatch;
    }

    public string Operation { get; }

    public string OpId { get; }

    public IReadOnlyList<(string Key, object? Value)> Properties { get; }

    public Stopwatch Stopwatch { get; }

    /// <summary>
    /// Renders the scope's correlation prefix in the form
    /// <c>[OpId=a1b2c3d4 Operation=ImportActiveFile UserId=u123]</c>.
    /// Returns an empty string when there is nothing meaningful to render.
    /// A property whose key is <c>"Op"</c> is skipped when its value equals
    /// <see cref="Operation"/> — otherwise the prefix would render
    /// <c>Op=ImportImport</c>-style duplicates from legacy call sites.
    /// </summary>
    public string FormatPrefix()
    {
        var sb = new StringBuilder(64);
        sb.Append("[OpId=").Append(OpId);
        if (!string.IsNullOrEmpty(Operation))
        {
            sb.Append(" Op=").Append(Operation);
        }
        if (Properties is { Count: > 0 })
        {
            foreach (var (key, value) in Properties)
            {
                if (key == "Op" && Equals(value, Operation))
                {
                    continue;
                }
                sb.Append(' ').Append(key).Append('=').Append(value);
            }
        }
        sb.Append(']');
        return sb.ToString();
    }
}
