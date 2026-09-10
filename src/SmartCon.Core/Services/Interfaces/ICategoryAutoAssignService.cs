using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// FamilyManager-side orchestration of the auto-assignment gate (#241):
/// loads the persisted rule groups + attribute definitions once per
/// dialog (<see cref="PreloadAsync"/>), then evaluates each family's
/// import snapshot synchronously via the pure
/// <see cref="ICategoryAutoAssignEngine"/>. No Revit API.
/// </summary>
public interface ICategoryAutoAssignService
{
    /// <summary>Loads all enabled rule groups and the attribute-name
    /// dictionary. Call once per batch dialog; the snapshot is immutable
    /// for the dialog's lifetime (mid-dialog rule edits are impossible —
    /// the dialog is modal; documented limitation).</summary>
    Task<CategoryAutoAssignPreloaded> PreloadAsync(CancellationToken ct = default);

    /// <summary>Evaluates one family against the preloaded rules. Exactly
    /// one snapshot is non-null (mirrors the import row shape).
    /// <paramref name="familyNameOverride"/> lets the batch dialog pass
    /// the row's current (possibly renamed) display name; when null the
    /// snapshot's own family name is used.</summary>
    CategoryAutoAssignResult Evaluate(
        CategoryAutoAssignPreloaded preloaded,
        FamilySnapshot? loadableSnapshot,
        SystemFamilySnapshot? systemSnapshot,
        string? familyNameOverride = null);
}
