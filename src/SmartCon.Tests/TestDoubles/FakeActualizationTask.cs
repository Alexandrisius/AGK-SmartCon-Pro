using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Tests.TestDoubles;

/// <summary>
/// Hand-written <see cref="IDatabaseActualizationTask"/> fake: configurable
/// pending count/keys, call counters, fault injection per method.
/// </summary>
public sealed class FakeActualizationTask : IDatabaseActualizationTask
{
    public FakeActualizationTask(string id, int order, bool isCritical = true)
    {
        Id = id;
        Order = order;
        IsCritical = isCritical;
    }

    public string Id { get; }
    public int Order { get; }
    public bool IsCritical { get; }
    public bool RequiresExtraction { get; set; } = true;

    public int PendingCount { get; set; }
    public NewerOnlyPendingInfo NewerPending { get; set; } = NewerOnlyPendingInfo.None;
    public int FileFreeResult { get; set; }
    public HashSet<string> PendingKeys { get; } = new(StringComparer.Ordinal);

    public int CountCalls { get; private set; }
    public int NewerCountCalls { get; private set; }
    public int DetectionCalls { get; private set; }
    public int FileFreeCalls { get; private set; }
    public int ApplyCalls { get; private set; }
    public int FailureCalls { get; private set; }

    public List<string> AppliedGroupKeys { get; } = new();
    public List<(string Key, ActualizationFailureKind Kind)> Failures { get; } = new();

    public Exception? CountException { get; set; }
    public Exception? DetectionException { get; set; }
    public Exception? FileFreeException { get; set; }
    public Exception? ApplyException { get; set; }

    public Task<int> CountPendingAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        CountCalls++;
        if (CountException is not null) throw CountException;
        return Task.FromResult(PendingCount);
    }

    public Task<NewerOnlyPendingInfo> GetNewerOnlyPendingAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        NewerCountCalls++;
        if (CountException is not null) throw CountException;
        return Task.FromResult(NewerPending);
    }

    public Task<IReadOnlyCollection<string>> LoadPendingGroupKeysAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        DetectionCalls++;
        if (DetectionException is not null) throw DetectionException;
        return Task.FromResult<IReadOnlyCollection<string>>(PendingKeys);
    }

    public Task<int> RunFileFreePassAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        FileFreeCalls++;
        if (FileFreeException is not null) throw FileFreeException;
        return Task.FromResult(FileFreeResult);
    }

    public Task ApplyAsync(FamilyActualizationContext context, CancellationToken ct = default)
    {
        ApplyCalls++;
        AppliedGroupKeys.Add(context.Group.Key);
        if (ApplyException is not null) throw ApplyException;
        return Task.CompletedTask;
    }

    public Task HandleGroupFailureAsync(
        ActualizationGroup group, ActualizationFailureKind kind, CancellationToken ct = default)
    {
        FailureCalls++;
        Failures.Add((group.Key, kind));
        return Task.CompletedTask;
    }
}
