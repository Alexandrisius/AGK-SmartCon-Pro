using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// Partial class extension for extraction helpers.
/// Wraps the sync <see cref="IFamilyDataExtractionService.ExtractFromManagedFile"/>
/// call (which invokes Revit API) in <see cref="IFamilyManagerAwaitableEvent"/>
/// to marshal onto the Revit UI thread (I-01).
/// </summary>
public sealed partial class FamilyManagerMainViewModel
{
    /// <summary>
    /// Single entry point for all managed-storage import paths. Reads baked-in
    /// family types and parameters from the managed .rfa (ADR-033).
    /// </summary>
    private async Task<FamilyExtractionResult> ExtractFromManagedFileAsync(
        string managedRfaPath,
        IReadOnlyList<string> expectedParameterNames,
        CancellationToken ct)
    {
        return await _awaitableEvent
            .RaiseAsync<FamilyExtractionResult>(
                _ => _extractionService.ExtractFromManagedFile(managedRfaPath, expectedParameterNames, ct),
                ct)
            .ConfigureAwait(false);
    }
}
