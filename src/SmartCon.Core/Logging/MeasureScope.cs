using System.Diagnostics;

namespace SmartCon.Core.Logging;

/// <summary>
/// A <see cref="LogScope"/> augmented with a stopwatch so callers can
/// read the elapsed time at any point during the operation, not only
/// when the scope is disposed.
/// </summary>
/// <remarks>
/// Returned by <see cref="SmartConLogger.Measure(string)"/>. Implements
/// <see cref="IDisposable"/> so that
/// <c>using var ms = SmartConLogger.Measure("Op")</c> works as expected:
/// on <see cref="Dispose"/> the stopwatch stops, the
/// <c>=== END elapsed=… ===</c> footer is emitted, and the scope is
/// popped off the ambient stack. Callers that want the elapsed time in
/// a user-visible message before the scope ends can read
/// <see cref="GetElapsedMilliseconds"/> at any point.
/// </remarks>
public sealed class MeasureScope : LogScope, IDisposable
{
    private bool _disposed;

    internal MeasureScope(string operation, IReadOnlyList<(string Key, object? Value)> properties, string opId, Stopwatch stopwatch)
        : base(operation, properties, opId, stopwatch)
    {
    }

    /// <summary>
    /// Elapsed time in milliseconds since this scope was opened.
    /// Safe to call from any thread; the underlying <see cref="Stopwatch"/>
    /// uses <see cref="Stopwatch.GetTimestamp"/> which is thread-safe for
    /// concurrent reads.
    /// </summary>
    public double GetElapsedMilliseconds() => Stopwatch.Elapsed.TotalMilliseconds;

    /// <summary>
    /// Stop the stopwatch, emit the <c>=== END elapsed=… ===</c> footer
    /// and pop the scope from the ambient stack. Idempotent.
    /// </summary>
    /// <remarks>
    /// The footer is written <b>before</b> the scope is popped so the
    /// ambient prefix chain still contains this scope. We use
    /// <c>ComposeScopePrefix()</c> (the chain) instead of
    /// <c>FormatPrefix()</c> (this scope only) to avoid visual duplicates
    /// when the chain contains exactly one scope — e.g. an outer
    /// <c>Measure("Init")</c> at the top of the stack: the chain
    /// already renders <c>[OpId=X Op=Init]</c>, so the footer line
    /// becomes <c>[OpId=X Op=Init] === END elapsed=… ===</c> instead of
    /// the duplicated <c>[OpId=X Op=Init] [OpId=X Op=Init] === END ...</c>.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stopwatch.Stop();
        SmartConLogger.WriteMain("INF", $" === END elapsed={Stopwatch.Elapsed.TotalMilliseconds:F1}ms ===");
        LogScopeProvider.Pop();
    }
}

