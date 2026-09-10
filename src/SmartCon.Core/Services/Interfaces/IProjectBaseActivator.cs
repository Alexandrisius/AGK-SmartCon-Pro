using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Activates the database connection that best matches the currently active
/// Revit document, according to the project-base binding rules in the
/// FamilyManager registry. Pure orchestrator: depends on
/// <see cref="IDatabaseManager"/> (for list/switch) and
/// <see cref="IProjectBaseBindingEvaluator"/> (for matching) — never touches
/// Revit API directly (decision A11, see #119).
/// </summary>
public interface IProjectBaseActivator
{
    /// <summary>
    /// For the document with file path <paramref name="currentFilePath"/>,
    /// pick the first project-scoped base whose binding matches, switch the
    /// active database to it, and return its connection id. If no project
    /// base matches: keep the current active base when it is a
    /// <see cref="BaseType.General"/> one (the user's manual selection is
    /// preserved); otherwise (no active base, or the active project base no
    /// longer matches) fall back to the first <see cref="BaseType.General"/>
    /// connection. If neither kind has any candidate, leave the active
    /// database unchanged and return <c>null</c>.
    /// </summary>
    /// <returns>The id of the connection that was activated (or already
    /// active), or <c>null</c> if nothing was changed.</returns>
    Task<string?> ActivateForDocumentAsync(string currentFilePath, CancellationToken ct = default);
}