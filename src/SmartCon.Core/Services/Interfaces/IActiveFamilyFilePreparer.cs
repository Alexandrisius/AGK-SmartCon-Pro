using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Prepares the currently active Revit family document for import into
/// the FamilyManager catalog. The implementation is responsible for
/// saving the document to a temp staging folder and copying any
/// Type Catalog sidecar next to the saved file.
///
/// All Revit API access must happen on the Revit UI thread (I-01).
/// Implementations are expected to dispatch via
/// <see cref="IFamilyManagerAwaitableEvent"/>.
/// </summary>
public interface IActiveFamilyFilePreparer
{
    /// <summary>
    /// Inspects the active document and, if it is a family document,
    /// saves it to a temp folder and copies the .txt sidecar if present.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A populated <see cref="SmartCon.Core.Models.FamilyManager.ActiveFamilyPreparationResult"/>
    /// for family documents, or <c>null</c> when the active document is
    /// not a family / there is no active document / saving failed.
    /// </returns>
    Task<ActiveFamilyPreparationResult?> PrepareActiveFamilyAsync(CancellationToken ct = default);
}
