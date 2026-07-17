using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Tests.TestDoubles;

/// <summary>
/// <see cref="IDatabaseUpdateStateService"/> stub for view-model tests:
/// clean database by default (every gate passes), configurable via
/// properties. Refresh/Update just flip counters — no coordinator involved.
/// </summary>
public sealed class FakeDatabaseUpdateStateService : IDatabaseUpdateStateService
{
    public bool IsUpdateRequired { get; set; }
    public int PendingCount { get; set; }
    public bool IsRunning { get; set; }
    public bool EnsureResult { get; set; } = true;

    public int RefreshCalls { get; private set; }
    public int ResetCalls { get; private set; }
    public int EnsureCalls { get; private set; }
    public int UpdateCalls { get; private set; }

    public event EventHandler? StateChanged;

    public Task RefreshAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        RefreshCalls++;
        return Task.CompletedTask;
    }

    public void Reset() => ResetCalls++;

    public Task<bool> EnsureUpToDateAsync()
    {
        EnsureCalls++;
        return Task.FromResult(EnsureResult);
    }

    public Task UpdateAsync()
    {
        UpdateCalls++;
        return Task.CompletedTask;
    }

    public void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
