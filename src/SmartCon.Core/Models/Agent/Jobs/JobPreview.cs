namespace SmartCon.Core.Models.Agent.Jobs;

/// <summary>
/// Frozen preview envelope. Apply is allowed only when the presented digest,
/// document id, count and expiry all match the stored preview (fail-closed).
/// </summary>
public sealed class JobPreview
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string Action { get; init; }

    public required string DocumentId { get; init; }

    public int Count { get; init; }

    public int WillChangeCount { get; init; }

    public int SkipCount { get; init; }

    public int FailedCount { get; init; }

    public IReadOnlyList<JobPreviewSample> Sample { get; init; } = Array.Empty<JobPreviewSample>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>Hash of the sorted frozen unit keys + args: detects selection drift.</summary>
    public required string SelectorHash { get; init; }

    /// <summary>Hash of the canonical action spec.</summary>
    public required string SpecHash { get; init; }

    /// <summary>Hash binding all of the above + expiry; the apply token.</summary>
    public required string Digest { get; init; }

    public DateTime ExpiresAtUtc { get; init; }

    public bool IsExpired(DateTime utcNow) => utcNow >= ExpiresAtUtc;
}

public sealed class JobPreviewSample
{
    public string? UnitKey { get; init; }

    public string? Label { get; init; }

    public string? OldValue { get; init; }

    public string? NewValue { get; init; }

    public bool WillChange { get; init; }

    /// <summary>Item outcome at preview time: previewed, skipped or failed.</summary>
    public string? Status { get; init; }

    /// <summary>Why this unit cannot be applied (failed samples only).</summary>
    public string? ErrorCode { get; init; }

    public string? ErrorDetail { get; init; }
}