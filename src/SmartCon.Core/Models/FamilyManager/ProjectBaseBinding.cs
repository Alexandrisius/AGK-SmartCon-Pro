using SmartCon.Core.Models;

namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Binding of a <see cref="BaseType.Project"/> database to a Revit project
/// file name. Powered by the shared <see cref="FileNameTemplate"/> engine
/// (see ADR-045) — the same parser used by ProjectManagement.ShareProject —
/// kept in Core to avoid any FamilyManager → ProjectManagement dependency.
/// </summary>
/// <param name="Template">Block parser template (delimiters / fixed width / markers).</param>
/// <param name="FieldLibrary">Field definitions with validation rules used both
/// to evaluate template matches against the current document name and to drive
/// the user-facing editor in <c>ProjectBaseRulesEditorView</c>.</param>
public sealed record ProjectBaseBinding(
    FileNameTemplate Template,
    IReadOnlyList<FieldDefinition> FieldLibrary)
{
    public static ProjectBaseBinding Empty => new(FileNameTemplate.Empty, []);
}