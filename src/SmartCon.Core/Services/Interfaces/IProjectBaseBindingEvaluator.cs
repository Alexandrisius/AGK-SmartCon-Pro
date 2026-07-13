using System.Collections.Generic;
using SmartCon.Core.Models;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Outcome of matching a <see cref="ProjectBaseBinding"/> against the file
/// name of the currently active Revit document. See #119 / ADR-045.
/// </summary>
public enum ProjectBaseMatchKind
{
    /// <summary>Binding is null or the connection is <see cref="BaseType.General"/> — matching is not applicable.</summary>
    NotApplicable,

    /// <summary>File name parsed through the binding template and every field passed its <see cref="FieldDefinition"/> validations.</summary>
    Match,

    /// <summary>File name either could not be parsed by the template or one of the fields failed validation.</summary>
    Mismatch
}

/// <summary>
/// Detailed evaluation result. <see cref="ParsedValues"/> is populated only
/// for <see cref="ProjectBaseMatchKind.Match"/> / <see cref="ProjectBaseMatchKind.Mismatch"/>
/// and exposes the field-to-value mapping extracted by the parser so the
/// caller can both display the user-facing reason and pick a fallback.
/// </summary>
public sealed record ProjectBaseMatch(
    ProjectBaseMatchKind Kind,
    string? Reason = null,
    IReadOnlyDictionary<string, string>? ParsedValues = null);

/// <summary>
/// Pure-C# evaluator (no Revit API touch) that reuses the shared
/// <see cref="IFileNameParser"/> engine to decide whether a Revit document
/// file path matches a project base binding. Lives in Core so FamilyManager
/// can depend on it without pulling in Revit UI (ADR-045).
/// </summary>
public interface IProjectBaseBindingEvaluator
{
    /// <summary>
    /// Evaluate <paramref name="binding"/> against <paramref name="filePath"/>.
    /// Returns <see cref="ProjectBaseMatchKind.NotApplicable"/> when
    /// <paramref name="binding"/> is null — the caller is then expected to
    /// fall back to <see cref="BaseType.General"/> bases.
    /// </summary>
    ProjectBaseMatch Evaluate(ProjectBaseBinding? binding, string? filePath);
}