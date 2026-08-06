namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Identity of one dependency family discovered at Phase-1 prepare time
/// (ADR-066). Carries the Revit-side identity (<see cref="FamilyUniqueId"/>)
/// so the batch pipeline can prepare the dependency through the standard
/// loadable-from-project path (EditFamily by UniqueId, never by name — I-05),
/// plus the routing-rule token it was discovered from.
/// </summary>
/// <param name="Kind">Dependency class — see <see cref="FamilyDependencyKind"/>.</param>
/// <param name="PartName">
/// Original routing-rule token "Family:Type" for
/// <see cref="FamilyDependencyKind.Routing"/> dependencies; <c>null</c> for
/// other kinds.
/// </param>
/// <param name="FamilyUniqueId">UniqueId of the dependency Family in the source document.</param>
/// <param name="FamilyName">Display name of the dependency Family.</param>
/// <param name="CategoryName">Revit category display name of the dependency Family.</param>
public sealed record FamilyDependencyDescriptor(
    string Kind,
    string? PartName,
    string FamilyUniqueId,
    string FamilyName,
    string? CategoryName);
