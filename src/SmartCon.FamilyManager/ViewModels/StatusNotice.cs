using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// One entry of the clickable status-badge details dialog (#210):
/// a short localized <see cref="Title"/>, an optional bullet list of
/// concrete family names (<see cref="Items"/> — rendered «• Name» in
/// semi-bold so the actual dependencies stand out), and an optional
/// guidance <see cref="Explanation"/> (what to do next, no repeated
/// names/titles). Built by the row/node view-models; rendered by
/// StatusDetailsView. This record is the single canonical shape for ALL
/// current and future status badges — a new badge = a new StatusNotice
/// entry, never a new ad-hoc icon.
/// </summary>
public sealed record StatusNotice(
    StatusNoticeSeverity Severity,
    string Title,
    string? Explanation,
    IReadOnlyList<string>? Items = null)
{
    public bool HasItems => Items is { Count: > 0 };

    public bool HasExplanation => !string.IsNullOrEmpty(Explanation);
}
