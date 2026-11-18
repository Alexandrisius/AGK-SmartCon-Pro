using SmartCon.Core.Models.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// One row of a write-tool report (changed / skipped / failed).
/// </summary>
public sealed class WriteItemReport
{
    public long? Id { get; set; }

    public string? Name { get; set; }

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    public string? ErrorCode { get; set; }

    public string? Reason { get; set; }

    public string? Detail { get; set; }
}

public sealed class WriteBatch
{
    public List<WriteItemReport> Changed { get; } = new();

    public List<WriteItemReport> Skipped { get; } = new();

    public List<WriteItemReport> Failed { get; } = new();

    public int Total => Changed.Count + Skipped.Count + Failed.Count;
}

/// <summary>
/// Result of executing a write tool on the Revit thread. Collected inside the
/// transaction, persisted (change set) and serialized by
/// <see cref="AgentWriteToolBase"/> after the API call returns.
/// </summary>
public sealed class WriteOutcome
{
    public bool Success { get; set; } = true;

    public string? ErrorCode { get; set; }

    public string? Message { get; set; }

    public int AffectedCount { get; set; }

    /// <summary>True when the model was actually modified (never for a dry run).</summary>
    public bool ModelChanged { get; set; }

    /// <summary>Set by atomic execution when the batch must be rolled back.</summary>
    public bool Aborted { get; set; }

    public string? DocumentPath { get; set; }

    public string? RevitVersion { get; set; }

    public string? Description { get; set; }

    /// <summary>undo_last reverses a change set instead of recording a new one.</summary>
    public bool SuppressChangeSet { get; set; }

    /// <summary>Change set to mark as rolled back after a successful commit.</summary>
    public Guid? RollBackChangeSetId { get; set; }

    public List<string> Warnings { get; } = new();

    /// <summary>Change items recorded for <c>undo_last</c>.</summary>
    public List<ChangeItem> Items { get; } = new();

    public WriteBatch Batch { get; } = new();

    /// <summary>Tool-specific payload, merged into the tool result data.</summary>
    public Dictionary<string, object?> Data { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static WriteOutcome Fail(string errorCode, string message, string? hint = null)
    {
        var outcome = new WriteOutcome
        {
            Success = false,
            ErrorCode = errorCode,
            Message = string.IsNullOrEmpty(hint) ? message : message + " " + hint
        };
        return outcome;
    }
}

/// <summary>View range description in millimetres relative to levels.</summary>
internal sealed class ViewRangeSpec
{
    public ViewRangePlaneSpec? Top { get; set; }

    public ViewRangePlaneSpec? CutPlane { get; set; }

    public ViewRangePlaneSpec? Bottom { get; set; }

    public ViewRangePlaneSpec? ViewDepth { get; set; }

    public bool IsEmpty =>
        Top is null && CutPlane is null && Bottom is null && ViewDepth is null;
}

internal sealed class ViewRangePlaneSpec
{
    public long? LevelId { get; set; }

    public double? OffsetMm { get; set; }
}

/// <summary>Crop region description; extents are millimetres in the view frame.</summary>
internal sealed class ViewCropSpec
{
    public bool? Active { get; set; }

    public bool? Visible { get; set; }

    public double[]? MinMm { get; set; }

    public double[]? MaxMm { get; set; }

    public long? CopyFromViewId { get; set; }

    public bool IsEmpty => Active is null && Visible is null && MinMm is null
        && MaxMm is null && CopyFromViewId is null;
}

internal sealed class ViewCropState
{
    public bool Active { get; set; }

    public bool Visible { get; set; }

    /// <summary>True when the view crop is a non-rectangular sketch.</summary>
    public bool ShapeSet { get; set; }

    /// <summary>
    /// Polygon points of the crop sketch in millimetres (view frame), implicitly
    /// closed. Without them an undo of a shape → rectangle transition cannot
    /// restore the sketch: View.CropBox has no effect on non-rectangular crops.
    /// </summary>
    public List<double[]>? ShapeMm { get; set; }

    public double[]? MinMm { get; set; }

    public double[]? MaxMm { get; set; }
}

internal sealed class ViewVisibilitySpec
{
    public List<CategoryVisibilitySpec>? Categories { get; set; }

    public List<WorksetVisibilitySpec>? Worksets { get; set; }

    public bool IsEmpty => (Categories is null || Categories.Count == 0)
        && (Worksets is null || Worksets.Count == 0);
}

internal sealed class CategoryVisibilitySpec
{
    public string? Category { get; set; }

    public long? CategoryId { get; set; }

    public bool Hidden { get; set; } = true;
}

internal sealed class WorksetVisibilitySpec
{
    public string? Name { get; set; }

    public long? WorksetId { get; set; }

    public bool Hidden { get; set; } = true;
}

internal sealed class ViewVisibilityState
{
    public List<long> HiddenCategoryIds { get; set; } = new();

    public List<int> HiddenWorksetIds { get; set; } = new();
}

internal sealed class ViewSettingsSpec
{
    public string? NewName { get; set; }

    public long? TemplateId { get; set; }

    public bool? OverwriteTemplate { get; set; }

    public ViewRangeSpec? ViewRange { get; set; }

    public ViewCropSpec? Crop { get; set; }

    public ViewVisibilitySpec? Visibility { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(NewName) && TemplateId is null
        && (ViewRange is null || ViewRange.IsEmpty)
        && (Crop is null || Crop.IsEmpty)
        && (Visibility is null || Visibility.IsEmpty);
}

internal sealed class ConfigureViewItem
{
    public long ViewId { get; set; }

    public string? NewName { get; set; }

    public long? TemplateId { get; set; }

    public bool OverwriteTemplate { get; set; }

    public ViewRangeSpec? ViewRange { get; set; }

    public ViewCropSpec? Crop { get; set; }

    public ViewVisibilitySpec? Visibility { get; set; }
}

internal sealed class RenameViewItem
{
    public long ViewId { get; set; }

    public string? NewName { get; set; }
}

internal sealed class RenameLevelItem
{
    public long LevelId { get; set; }

    public string? NewName { get; set; }
}

internal sealed class ParameterWriteItem
{
    public long ElementId { get; set; }

    public string? Parameter { get; set; }

    public string? Guid { get; set; }

    public string? Value { get; set; }

    public string? Scope { get; set; }

    public string? Unit { get; set; }
}

internal sealed class RemapValue
{
    public string? Name { get; set; }

    public string? Guid { get; set; }

    public string? Scope { get; set; }
}

internal sealed class RemapMapping
{
    public RemapValue? Source { get; set; }

    public RemapValue? Target { get; set; }
}

internal sealed class RemapFilter
{
    public List<string>? Categories { get; set; }

    public List<long>? LevelIds { get; set; }

    public List<long>? TypeIds { get; set; }

    public int? Limit { get; set; }
}
