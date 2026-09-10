namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Declares that one batch-import row (the CHILD carrying this link) is a
/// dependency of another row (the PARENT, addressed by its
/// <see cref="ParentSourcePath"/> = <c>PreparedFamilyItem.SourcePath</c> /
/// <c>FamilyBatchImportItem.FilePath</c>). Links are produced during
/// Phase-1 prepare (ADR-066), ride through the batch dialog unchanged and
/// are consumed by the Phase-3 executor, which persists them to
/// <c>family_dependencies</c> after both sides are imported.
/// </summary>
/// <param name="ParentSourcePath">
/// Source path of the parent row (<c>"system://..."</c> for system
/// categories in E1; <c>"loadable://..."</c> for nested-shared parents in E2).
/// </param>
/// <param name="Kind">Dependency class — see <see cref="FamilyDependencyKind"/>.</param>
/// <param name="PartName">
/// Original routing-rule token "Family:Type" for
/// <see cref="FamilyDependencyKind.Routing"/> links; <c>null</c> otherwise.
/// </param>
public sealed record FamilyDependencyLink(
    string ParentSourcePath,
    string Kind,
    string? PartName);
