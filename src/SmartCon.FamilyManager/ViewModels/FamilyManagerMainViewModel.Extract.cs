using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// Partial class extension for the extraction helpers introduced by issue #66 fix.
/// Wraps the sync <see cref="IFamilyDataExtractionService.ExtractFromManagedFile"/>
/// call (which invokes Revit API) in <see cref="IFamilyManagerAwaitableEvent"/>
/// to marshal onto the Revit UI thread (I-01).
/// </summary>
public sealed partial class FamilyManagerMainViewModel
{
    /// <summary>
    /// Single entry point for all managed-storage import paths. Replaces the
    /// three duplicated <c>_extractionService.Extract(absolutePath, [])</c>
    /// call sites. Detects a .txt sidecar and delegates to
    /// <c>ExtractWithTypeCatalog</c> when present (ADR-032), otherwise
    /// falls back to standard type extraction.
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
