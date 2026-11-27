using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Revit.Agent.Tools.Project;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// A category the caller asked to hide or show that Revit will not let the API
/// touch in this view. It stays visible; the report says so instead of burying
/// the fact in a warning next to a success=true row.
/// </summary>
public sealed class NotHidableCategory
{
    public required string Category { get; init; }

    public long? CategoryId { get; init; }

    public required string Reason { get; init; }
}

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

    /// <summary>Per-section result for composite writes (copy_view_settings).</summary>
    public List<SectionReport>? Sections { get; set; }

    /// <summary>
    /// Where the same write is actually allowed, when it is blocked by a view
    /// template: the template id. Lets the model retry in the same round instead
    /// of spending a call to find the controller (A5).
    /// </summary>
    public List<long>? SuggestedViewIds { get; set; }

    /// <summary>
    /// Template-controlled sections (viewRange/visibility/crop) that were NOT
    /// synchronized because the template has their Include checkbox off — the
    /// view keeps its own values. Reported by apply_view_template so the side
    /// effect of a detached setting is visible (#457, benchmark case 3).
    /// </summary>
    public List<string>? SkippedByTemplateInclude { get; set; }

    /// <summary>
    /// What the row did to the element, for reports whose rows are not parameter
    /// writes (a filter assigned to a view is not a view rename). Null for the
    /// plain old/new-value rows.
    /// </summary>
    public string? Action { get; set; }

    /// <summary>
    /// Own view filters the row's view template takes over (the template includes
    /// "V/G Overrides Filters"): they are replaced by the template's list.
    /// </summary>
    public List<string>? ReplacedOwnFilters { get; set; }

    /// <summary>
    /// Categories this row could not hide/show: they are still visible in the
    /// view. A plain warning next to success=true was easy to miss (owner
    /// feedback 2026-11-24: cameras and stair sub-categories stayed visible).
    /// </summary>
    public List<NotHidableCategory>? NotHidable { get; set; }

    /// <summary>
    /// The row was skipped because the SOURCE parameter had no value
    /// (map_family_parameters migration audits): distinguishes "nothing to
    /// copy" from "the target already matches" (empty vendor remark fields
    /// used to read as false duplicates of filled targets).
    /// </summary>
    public bool? SourceValueEmpty { get; set; }

    /// <summary>
    /// The PROJECT binding of a just-added shared parameter: in the project
    /// context the binding overrides the family's scope and group (PG_DATA →
    /// PG_TEXT of the pilot), and schedules read the binding's scope, not the
    /// family's. Null when the project has no binding for the GUID/name.
    /// </summary>
    public ProjectBindingNote? ProjectBinding { get; set; }
}

/// <summary>Project-level binding note for a shared family parameter.</summary>
public sealed class ProjectBindingNote
{
    public required string Scope { get; init; }

    public string? Group { get; init; }

    /// <summary>
    /// Resolves the project binding for a shared parameter: GUID first (the
    /// safe key), name as the fallback for the rare same-name non-shared
    /// binding. Null when the project does not bind it.
    /// </summary>
    public static ProjectBindingNote? Resolve(Document projectDoc, Guid? guid, string name)
    {
        try
        {
            foreach (var row in AgentProjectParameterReader.Collect(projectDoc))
            {
                var byGuid = guid is not null && row.Guid == guid;
                var byName = guid is null
                             && string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase);
                if (!byGuid && !byName)
                    continue;

                return new ProjectBindingNote
                {
                    Scope = row.BindingType,
                    Group = row.ParameterGroup
                };
            }
        }
        catch (Exception)
        {
            // A binding pass failure must not fail the add: the note is
            // informational.
        }

        return null;
    }
}

public sealed class SectionReport
{
    public required string Section { get; init; }

    /// <summary>copied | skipped | failed</summary>
    public required string Status { get; init; }

    public string? Reason { get; init; }
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

    /// <summary>
    /// DocumentVersion marker captured on the Revit thread when the tool ran; the
    /// tool result exposes it as <c>documentVersion</c>.
    /// </summary>
    public string? DocumentVersion { get; set; }

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

    /// <summary>
    /// Inverse mode: leave only these categories visible and hide every other
    /// category Revit lets the API toggle. The owner's case "hide everything
    /// except the system categories" used a blocklist and missed a stair
    /// category — an allow-list cannot miss it.
    /// </summary>
    public List<CategoryVisibilitySpec>? HideAllExcept { get; set; }

    public bool IsEmpty => (Categories is null || Categories.Count == 0)
        && (Worksets is null || Worksets.Count == 0)
        && (HideAllExcept is null || HideAllExcept.Count == 0);
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

    public List<long>? FamilyIds { get; set; }

    public List<string>? FamilyNames { get; set; }

    /// <summary>Exact elements to process (live v18: "repeat WITHOUT the failed
    /// items" needs a per-element filter — pass the failed element ids here).</summary>
    public List<long>? ElementIds { get; set; }

    public int? Limit { get; set; }
}
