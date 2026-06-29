using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// Partial class extension for extraction helpers.
/// Contains <see cref="SaveSharedNestedNamesAsync"/> — persistence of
/// shared-nested family names extracted alongside types and parameters.
/// </summary>
public sealed partial class FamilyManagerMainViewModel
{
    /// <summary>
    /// Persists the shared-nested family names extracted alongside types and
    /// parameters, so the load path can render real names in the SmartCon
    /// "Shared nested family — loading mode" dialog on Revit ≤ 2024.2 where
    /// the Revit API returns <c>null</c> for the shared family reference
    /// (REVIT-198137). No-op when the version id is missing (legacy item)
    /// or the extraction produced no names.
    ///
    /// ADR-034 §2: the names are now produced by
    /// <see cref="IFamilyDataExtractionService.ExtractFromManagedFile"/> in
    /// the same OpenDocumentFile+Close cycle as the type/parameter scan, so
    /// the user sees one family-upgrade dialog per .rfa instead of two (V2
    /// regression that this method now avoids).
    /// </summary>
    /// <param name="catalogItemId">Catalog item id (catalog_items.id).</param>
    /// <param name="versionId">Catalog version id (catalog_versions.id). When
    /// null we skip persistence — there is no row to write against and the
    /// caller (typically a legacy batch import) doesn't have a stable version
    /// handle.</param>
    /// <param name="sharedNames">Names produced by
    /// <see cref="FamilyExtractionResult.SharedNestedFamilyNamesSafe"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task SaveSharedNestedNamesAsync(
        string catalogItemId,
        string? versionId,
        IReadOnlyList<string>? sharedNames,
        CancellationToken ct)
    {
        if (versionId is null)
        {
            SmartConLogger.Debug(
                $"SaveSharedNestedNames: skipped (versionId is null — legacy item without stable version handle)");
            return;
        }
        if (sharedNames is null || sharedNames.Count == 0)
        {
            SmartConLogger.Debug(
                $"SaveSharedNestedNames: skipped (no shared-nested names extracted for CatalogItemId={catalogItemId}, VersionId={versionId})");
            return;
        }

        // Issue #77 verification (Debug): explicit per-extraction log so an
        // operator can confirm the catalog fallback list is being populated.
        SmartConLogger.Info(
            $"SaveSharedNestedNames: persisting {sharedNames.Count} shared-nested name(s) for " +
            $"CatalogItemId={catalogItemId}, VersionId={versionId} (REVIT-198137 fallback ready)");

        try
        {
            await _sharedNestedRepository
                .ReplaceForVersionAsync(catalogItemId, versionId, sharedNames, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Failed to persist shared-nested names for '{catalogItemId}' (v={versionId}): " +
                $"{ex.GetType().Name}: {ex.Message} " +
                "[Action: dialog will fall back to Revit API name only — re-import the family in Family Manager to refresh]");
        }
    }
}
