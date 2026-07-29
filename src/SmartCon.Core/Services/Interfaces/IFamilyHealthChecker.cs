using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Import-time family health check: detects system-level problems inside
/// a family document (broken formulas, per-type regeneration failures,
/// accumulated warnings) so corrupt families never reach the catalog.
/// Implemented in SmartCon.Revit; the <see cref="Document"/> parameter is
/// opaque to Core (I-09).
/// </summary>
public interface IFamilyHealthChecker
{
    /// <summary>
    /// Full check for a background-opened family document (UC-1 file
    /// import): iterates all types with <c>FamilyManager.CurrentType</c>
    /// + <c>Regenerate()</c> inside rolled-back transactions and collects
    /// failure messages. The document is left unmodified. Warnings are
    /// swallowed from the Revit UI and surfaced in the report instead.
    /// </summary>
    FamilyHealthReport CheckFamilyDocument(Document familyDoc, CancellationToken ct = default);

    /// <summary>
    /// Lightweight check for the ACTIVE family document (UC-2 Family
    /// Editor import): reads accumulated document warnings only — no type
    /// switching, no UI flicker in the document the user is editing.
    /// </summary>
    FamilyHealthReport CheckActiveFamilyDocument(Document familyDoc);
}
