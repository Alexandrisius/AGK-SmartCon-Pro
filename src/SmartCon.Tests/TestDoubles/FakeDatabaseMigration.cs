using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Tests.TestDoubles;

/// <summary>
/// Hand-written <see cref="IDatabaseMigration"/> fake: configurable pending
/// sequence (each CountPendingAsync dequeues; the last value repeats), call
/// counters, optional shared run log and a fault injection for the count.
/// </summary>
public sealed class FakeDatabaseMigration : IDatabaseMigration
{
    private readonly Queue<int> _pendingSequence;

    public FakeDatabaseMigration(string id, int order, params int[] pendingSequence)
    {
        Id = id;
        Order = order;
        _pendingSequence = new Queue<int>(pendingSequence.Length > 0 ? pendingSequence : new[] { 0 });
    }

    public string Id { get; }
    public int Order { get; }
    public int CountCalls { get; private set; }
    public int RunCalls { get; private set; }
    public List<string>? SharedRunLog { get; set; }
    public Exception? CountException { get; set; }
    public Exception? RunException { get; set; }

    /// <summary>Pending value returned after the sequence is exhausted.</summary>
    public int TerminalPending { get; set; }

    public Task<int> CountPendingAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        CountCalls++;
        if (CountException is not null) throw CountException;
        var value = _pendingSequence.Count > 1
            ? _pendingSequence.Dequeue()
            : (_pendingSequence.Count == 1 ? _pendingSequence.Peek() : TerminalPending);
        return Task.FromResult(value);
    }

    public Task RunAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        RunCalls++;
        SharedRunLog?.Add(Id);
        if (RunException is not null) throw RunException;
        // A completed migration drains its pending queue to zero.
        _pendingSequence.Clear();
        _pendingSequence.Enqueue(TerminalPending);
        return Task.CompletedTask;
    }
}
