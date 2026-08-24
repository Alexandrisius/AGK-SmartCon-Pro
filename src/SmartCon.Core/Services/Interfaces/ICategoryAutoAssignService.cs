using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// FamilyManager-side orchestration of the auto-assignment gate (#241):
/// loads the persisted rule groups + attribute definitions, builds the
/// <see cref="CategoryAutoAssignInput"/> from the import snapshots and
/// runs the pure <see cref="ICategoryAutoAssignEngine"/>. No Revit API.
/// </summary>
public interface ICategoryAutoAssignService
{
    /// <summary>Evaluates one family against the configured rules.
    /// Exactly one snapshot is non-null (mirrors the import row shape).
    /// <paramref name="familyNameOverride"/> lets the batch dialog pass the
    /// row's current (possibly renamed) display name; when null the
    /// snapshot's own family name is used. Returns
    /// <see cref="CategoryAutoAssignOutcome.NoMatch"/> when no rules are
    /// configured.</summary>
    Task<CategoryAutoAssignResult> EvaluateAsync(
        FamilySnapshot? loadableSnapshot,
        SystemFamilySnapshot? systemSnapshot,
        string? familyNameOverride = null,
        CancellationToken ct = default);
}
